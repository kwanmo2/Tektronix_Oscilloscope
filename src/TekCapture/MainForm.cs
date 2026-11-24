using System.Diagnostics;
using System.Text.Json;

namespace TekCapture;

public sealed class MainForm : Form
{
    private readonly ComboBox _cboRsrc = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnRefresh = new() { Text = "검색", AutoSize = true };
    private readonly Button _btnConnect = new() { Text = "연결", AutoSize = true };
    private readonly Label _lblIdn = new() { Text = "연결 안 됨", AutoSize = true, Padding = new Padding(8, 7, 0, 0) };

    private readonly CheckBox[] _chk = TekScope.AllSources.Select(s => new CheckBox { Text = s, Tag = s, AutoSize = true, ForeColor = Color.Black }).ToArray();
    private readonly CheckBox _chkAutoCh = new() { Text = "표시 중인 채널 자동 선택", Checked = true, AutoSize = true };
    private readonly CheckBox _chkStopFirst = new() { Text = "캡처 전 Stop (채널 간 동기)", AutoSize = true };
    private readonly CheckBox _chkInk = new() { Text = "Ink Saver (흰 배경)", AutoSize = true };
    private readonly CheckBox _chkAutoSave = new() { Text = "캡처 시 자동 저장", Checked = true, AutoSize = true };
    private readonly TextBox _txtFolder = new() { Width = 200 };

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _tabScreen = new("화면");
    private readonly TabPage _tabWave = new("파형");
    private readonly PictureBox _pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 30, 30) };
    private readonly WaveformPanel _wave = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _cboRecLen = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblHoriz = new() { AutoSize = true, MaximumSize = new Size(215, 0), ForeColor = Color.DimGray };
    private readonly TextBox _log = new() { Dock = DockStyle.Bottom, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f) };

    private readonly List<Control> _scopeButtons = [];
    private TekScope? _scope;
    private byte[]? _lastPng;
    private List<Waveform>? _lastWaves;
    private bool _busy;

    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TekCapture", "settings.json");
    private sealed record Settings(string? Folder, string? Resource, bool InkSaver, bool AutoSave, bool StopFirst, Dictionary<string, string>? Labels = null);
    private bool _syncRecLen;

    public MainForm()
    {
        Text = "TekCapture — Tektronix 파형/화면 캡처";
        Size = new Size(1200, 800);
        MinimumSize = new Size(900, 600);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);

        // --- top bar
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6), WrapContents = false };
        top.Controls.AddRange([new Label { Text = "장치:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, _cboRsrc, _btnRefresh, _btnConnect, _lblIdn]);

        // --- left panel
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 240, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        Button Btn(string text, Func<Task> op, bool needsScope = true)
        {
            var b = new Button { Text = text, Width = 215, Height = 34 };
            b.Click += async (_, _) => await RunOp(text, op);
            if (needsScope) _scopeButtons.Add(b);
            return b;
        }
        Label Hdr(string t) => new() { Text = t, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };

        left.Controls.Add(Hdr("캡처"));
        left.Controls.Add(Btn("화면 캡처 (PNG)", CaptureScreen));
        left.Controls.Add(Btn("파형 캡처 (데이터)", CaptureWaveforms));
        left.Controls.Add(Btn("화면 + 파형 모두", async () => { await CaptureScreen(); await CaptureWaveforms(); }));

        left.Controls.Add(Hdr("채널"));
        var chPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 215, Margin = new Padding(0) };
        foreach (var c in _chk) { c.BackColor = WaveformView.ColorOf((string)c.Tag!); c.Margin = new Padding(2); chPanel.Controls.Add(c); }
        left.Controls.Add(chPanel);
        left.Controls.Add(_chkAutoCh);
        left.Controls.Add(_chkStopFirst);
        left.Controls.Add(_chkInk);

        left.Controls.Add(Hdr("수평 (스코프 설정)"));
        foreach (var n in TekScope.RecordLengths) _cboRecLen.Items.Add($"{n:N0} pts");
        var recRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        recRow.Controls.AddRange([new Label { Text = "레코드 길이", AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, _cboRecLen]);
        _scopeButtons.Add(_cboRecLen);
        left.Controls.Add(recRow);
        left.Controls.Add(_lblHoriz);
        _cboRecLen.SelectedIndexChanged += async (_, _) =>
        {
            if (_syncRecLen || _cboRecLen.SelectedIndex < 0) return;
            int n = TekScope.RecordLengths[_cboRecLen.SelectedIndex];
            await RunOp($"레코드 길이 {n:N0}", async () =>
            {
                var s = RequireScope();
                await Task.Run(() => s.SetRecordLength(n));
                await UpdateHorizontal();
            });
        };

        left.Controls.Add(Hdr("파일"));
        left.Controls.Add(Btn("파형 불러오기 (CSV)…", LoadCsvDialog, false));
        left.Controls.Add(_chkAutoSave);
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var btnBrowse = new Button { Text = "...", Width = 30, Height = 24 };
        btnBrowse.Click += (_, _) => BrowseFolder();
        folderRow.Controls.AddRange([_txtFolder, btnBrowse]);
        left.Controls.Add(folderRow);
        left.Controls.Add(Btn("현재 화면 PNG 저장", () => { SavePng(Stamp()); return Task.CompletedTask; }, false));
        left.Controls.Add(Btn("현재 파형 CSV 저장", SaveCsvAsync, false));
        left.Controls.Add(Btn("저장 폴더 열기", () => { Directory.CreateDirectory(Folder); Process.Start("explorer.exe", Folder); return Task.CompletedTask; }, false));

        left.Controls.Add(Hdr("수집 제어"));
        left.Controls.Add(Btn("Run", () => Task.Run(() => RequireScope().Run())));
        left.Controls.Add(Btn("Stop", () => Task.Run(() => RequireScope().Stop())));
        left.Controls.Add(Btn("Single", () => Task.Run(() => RequireScope().Single())));

        // --- center
        _tabScreen.Controls.Add(_pic);
        _tabWave.Controls.Add(_wave);
        _tabs.TabPages.AddRange([_tabScreen, _tabWave]);

        // dock order: fill first, then edges (last added docks first)
        Controls.Add(_tabs);
        Controls.Add(left);
        Controls.Add(_log);
        Controls.Add(top);

        _btnRefresh.Click += async (_, _) => await RunOp("장치 검색", RefreshResources);
        _btnConnect.Click += async (_, _) => await RunOp(_scope == null ? "연결" : "연결 해제", ToggleConnect);

        // drag & drop a CSV anywhere on the window
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 && f[0].EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0)
                await RunOp("파형 불러오기", () => LoadCsv(f[0]));
        };

        LoadSettings();
        _wave.LabelsChanged += (_, _) => SaveSettings();
        UpdateUi();
        Shown += async (_, _) =>
        {
            await RunOp("장치 검색", RefreshResources);
            if (_cboRsrc.Text.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                await RunOp("연결", ToggleConnect);
        };
        FormClosing += (_, _) => { SaveSettings(); _scope?.Dispose(); };
    }

    // ---------------------------------------------------------------- operations

    private TekScope RequireScope() => _scope ?? throw new InvalidOperationException("스코프가 연결되지 않았습니다.");
    private string Folder => string.IsNullOrWhiteSpace(_txtFolder.Text) ? DefaultFolder : _txtFolder.Text.Trim();
    private static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TekCapture");
    private string Stamp() => $"{_scope?.Model ?? "Scope"}_{DateTime.Now:yyyyMMdd_HHmmss}";

    private async Task RunOp(string name, Func<Task> op)
    {
        if (_busy) return;
        _busy = true;
        UpdateUi();
        UseWaitCursor = true;
        var sw = Stopwatch.StartNew();
        try
        {
            await op();
            Log($"{name} 완료 ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex)
        {
            Log($"{name} 실패: {ex.Message}");
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            UpdateUi();
        }
    }

    private async Task RefreshResources()
    {
        var list = await Task.Run(() => VisaSession.FindResources());
        string prev = _cboRsrc.Text;
        _cboRsrc.Items.Clear();
        // Tektronix (VID 0x0699) first
        foreach (var r in list.OrderByDescending(r => r.Contains("0x0699", StringComparison.OrdinalIgnoreCase)).ThenBy(r => r))
            _cboRsrc.Items.Add(r);
        Log($"VISA 리소스 {list.Count}개: {string.Join(", ", list)}");
        if (!string.IsNullOrEmpty(prev) && list.Contains(prev)) _cboRsrc.Text = prev;
        else if (_cboRsrc.Items.Count > 0) _cboRsrc.SelectedIndex = 0;
    }

    private async Task ToggleConnect()
    {
        if (_scope != null)
        {
            _scope.Dispose();
            _scope = null;
            _lblIdn.Text = "연결 안 됨";
            return;
        }
        string rsrc = _cboRsrc.Text.Trim();
        if (rsrc.Length == 0) throw new InvalidOperationException("장치를 선택하세요.");
        var scope = await Task.Run(() => new TekScope(rsrc));
        _scope = scope;
        _lblIdn.Text = scope.Idn;
        Log($"연결됨: {scope.Idn}");
        await UpdateDisplayedChannels();
        await UpdateHorizontal();
    }

    private async Task UpdateHorizontal()
    {
        var s = RequireScope();
        var h = await Task.Run(s.GetHorizontal);
        _syncRecLen = true;
        _cboRecLen.SelectedIndex = Array.IndexOf(TekScope.RecordLengths, h.RecordLength);
        _syncRecLen = false;
        _lblHoriz.Text = $"{Eng.Format(h.TimePerDiv, "s")}/div · {Eng.Format(h.SampleRate, "S/s")}\n" +
                         $"한 번 캡처 = {Eng.Format(h.Duration, "s")} ({h.RecordLength:N0} pts)";
    }

    private async Task UpdateDisplayedChannels()
    {
        var s = RequireScope();
        var shown = await Task.Run(s.DisplayedSources);
        foreach (var c in _chk) c.Checked = shown.Contains((string)c.Tag!);
    }

    private async Task CaptureScreen()
    {
        var s = RequireScope();
        bool ink = _chkInk.Checked;
        var png = await Task.Run(() => s.CaptureScreen(ink));
        _lastPng = png;
        using (var ms = new MemoryStream(png))
        using (var img = Image.FromStream(ms))
        {
            var old = _pic.Image;
            _pic.Image = new Bitmap(img);
            old?.Dispose();
        }
        _tabs.SelectedTab = _tabScreen;
        Log($"화면 캡처: {png.Length / 1024.0:F1} KB");
        if (_chkAutoSave.Checked) SavePng(Stamp());
    }

    private async Task CaptureWaveforms()
    {
        var s = RequireScope();
        if (_chkAutoCh.Checked) await UpdateDisplayedChannels();
        await UpdateHorizontal();
        var sources = _chk.Where(c => c.Checked).Select(c => (string)c.Tag!).ToList();
        if (sources.Count == 0) throw new InvalidOperationException("선택된 채널이 없습니다 (스코프에 표시된 채널이 없음).");
        bool stopFirst = _chkStopFirst.Checked;

        var progress = new Progress<string>(Log);
        var waves = await Task.Run(() =>
        {
            if (stopFirst) s.Stop();
            var list = new List<Waveform>();
            foreach (var src in sources)
            {
                var sw = Stopwatch.StartNew();
                var w = s.ReadWaveform(src);
                ((IProgress<string>)progress).Report($"  {src}: {w.Length:N0} pts, {sw.ElapsedMilliseconds} ms");
                list.Add(w);
            }
            return list;
        });
        _lastWaves = waves;
        _wave.SetWaveforms(waves);
        _tabs.SelectedTab = _tabWave;
        _tabWave.Text = "파형";
        if (_chkAutoSave.Checked) await SaveCsvAsync();
    }

    private async Task LoadCsvDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "파형 CSV 불러오기",
            Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            InitialDirectory = Directory.Exists(Folder) ? Folder : DefaultFolder,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await LoadCsv(dlg.FileName);
    }

    private async Task LoadCsv(string path)
    {
        Log($"불러오는 중: {path} ({new FileInfo(path).Length / 1e6:F1} MB)");
        var progress = new Progress<string>(Log);
        var waves = await Task.Run(() => CsvImport.Read(path, progress));
        _lastWaves = waves;
        _wave.SetWaveforms(waves, applySavedLabels: false);
        _tabs.SelectedTab = _tabWave;
        _tabWave.Text = $"파형 — {Path.GetFileName(path)}";
        foreach (var w in waves)
            Log($"  {w.DisplayName}: {w.Length:N0} pts, dt {Eng.Format(w.XIncr, w.XUnit)}, {Eng.Format(w.TStart, w.XUnit)} ~ {Eng.Format(w.TEnd, w.XUnit)}");
    }

    private void SavePng(string stamp)
    {
        if (_lastPng == null) throw new InvalidOperationException("저장할 화면 캡처가 없습니다.");
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, stamp + "_screen.png");
        File.WriteAllBytes(path, _lastPng);
        Log($"저장: {path}");
    }

    private async Task SaveCsvAsync()
    {
        var waves = _lastWaves ?? throw new InvalidOperationException("저장할 파형이 없습니다.");
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, Stamp() + "_wave.csv");
        await Task.Run(() => CsvExport.Write(path, waves));
        Log($"저장: {path}");
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Folder, UseDescriptionForTitle = true, Description = "저장 폴더 선택" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtFolder.Text = dlg.SelectedPath;
    }

    // ---------------------------------------------------------------- ui helpers

    private void UpdateUi()
    {
        bool connected = _scope != null;
        foreach (var b in _scopeButtons) b.Enabled = connected && !_busy;
        _btnConnect.Text = connected ? "연결 해제" : "연결";
        _btnConnect.Enabled = !_busy;
        _btnRefresh.Enabled = !_busy && !connected;
        _cboRsrc.Enabled = !connected;
    }

    private void Log(string msg)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    private void LoadSettings()
    {
        _txtFolder.Text = DefaultFolder;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var st = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (st == null) return;
            if (!string.IsNullOrWhiteSpace(st.Folder)) _txtFolder.Text = st.Folder;
            if (!string.IsNullOrWhiteSpace(st.Resource)) _cboRsrc.Text = st.Resource;
            _chkInk.Checked = st.InkSaver;
            _chkAutoSave.Checked = st.AutoSave;
            _chkStopFirst.Checked = st.StopFirst;
            if (st.Labels != null) _wave.SavedLabels = new(st.Labels, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* ignore corrupt settings */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(_txtFolder.Text, _cboRsrc.Text, _chkInk.Checked, _chkAutoSave.Checked, _chkStopFirst.Checked, _wave.SavedLabels)));
        }
        catch { }
    }
}
