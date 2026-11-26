using System.Diagnostics;
using System.Text.Json;

namespace TekCapture;

public sealed class MainForm : Form
{
    private readonly ComboBox _cboRsrc = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnRefresh = new Button { AutoSize = true }.L("검색", "Search");
    private readonly Button _btnConnect = new() { AutoSize = true };
    private readonly Label _lblIdn = new() { AutoSize = true, Padding = new Padding(8, 7, 0, 0) };

    private readonly CheckBox[] _chk = TekScope.AllSources.Select(s => new CheckBox { Text = s, Tag = s, AutoSize = true, ForeColor = Color.Black }).ToArray();
    private readonly CheckBox _chkAutoCh = new CheckBox { Checked = true, AutoSize = true }.L("표시 중인 채널 자동 선택", "Auto-select displayed channels");
    private readonly CheckBox _chkStopFirst = new CheckBox { AutoSize = true }.L("캡처 전 Stop (채널 간 동기)", "Stop before capture (sync channels)");
    private readonly CheckBox _chkInk = new CheckBox { AutoSize = true }.L("Ink Saver (흰 배경)", "Ink Saver (white background)");
    private readonly ToolStripMenuItem _miAutoSave = new ToolStripMenuItem { CheckOnClick = true, Checked = true }.L("캡처 시 자동 저장(&A)", "&Auto-save on capture");
    private readonly ToolStripMenuItem _miColors = new ToolStripMenuItem().L("채널 색상(&C)", "Channel &colors");
    private readonly ToolStripMenuItem _miLang = new("&Language");
    private TekScope.HorizontalInfo? _lastHoriz;
    private readonly ToolStripStatusLabel _stFolder = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, IsLink = true };
    private readonly ToolStripStatusLabel _stAutoSave = new();
    private string _folder = DefaultFolder;
    private readonly List<ToolStripItem> _scopeItems = [];

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
    /// <summary>CSV file that holds the waveforms shown in the viewer (null = not saved).</summary>
    private string? _lastCsv;
    private bool _busy;

    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TekCapture", "settings.json");
    private sealed record Settings(string? Folder, string? Resource, bool InkSaver, bool AutoSave, bool StopFirst, Dictionary<string, string>? Labels = null,
        Dictionary<string, string>? Colors = null, string? Language = null);
    private bool _syncRecLen;

    public MainForm()
    {
        Loc.Set(ReadLanguageSetting());
        this.L("TekCapture — Tektronix 파형/화면 캡처", "TekCapture — Tektronix waveform/screen capture");
        Size = new Size(1200, 800);
        MinimumSize = new Size(900, 600);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);

        // --- top bar
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6), WrapContents = false };
        top.Controls.AddRange([new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0) }.L("장치:", "Device:"), _cboRsrc, _btnRefresh, _btnConnect, _lblIdn]);

        // --- left panel
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 240, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        Button Btn(string ko, string en, Func<Task> op, bool needsScope = true)
        {
            var b = new Button { Width = 215, Height = 34 }.L(ko, en);
            b.Click += async (_, _) => await RunOp(b.Text, op);
            if (needsScope) _scopeButtons.Add(b);
            return b;
        }
        Label Hdr(string ko, string en) => new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) }.L(ko, en);

        left.Controls.Add(Hdr("캡처", "Capture"));
        left.Controls.Add(Btn("화면 캡처 (PNG)", "Capture screen (PNG)", CaptureScreen));
        left.Controls.Add(Btn("파형 캡처 (데이터)", "Capture waveforms (data)", CaptureWaveforms));
        left.Controls.Add(Btn("화면 + 파형 모두", "Screen + waveforms", async () => { await CaptureScreen(); await CaptureWaveforms(); }));

        left.Controls.Add(Hdr("채널", "Channels"));
        var chPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Width = 215, Margin = new Padding(0) };
        foreach (var c in _chk) { c.BackColor = WaveformView.ColorOf((string)c.Tag!); c.Margin = new Padding(2); chPanel.Controls.Add(c); }
        left.Controls.Add(chPanel);
        left.Controls.Add(_chkAutoCh);
        left.Controls.Add(_chkStopFirst);
        left.Controls.Add(_chkInk);

        left.Controls.Add(Hdr("수평 (스코프 설정)", "Horizontal (scope setting)"));
        foreach (var n in TekScope.RecordLengths) _cboRecLen.Items.Add($"{n:N0} pts");
        var recRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        recRow.Controls.AddRange([new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) }.L("레코드 길이", "Record length"), _cboRecLen]);
        _scopeButtons.Add(_cboRecLen);
        left.Controls.Add(recRow);
        left.Controls.Add(_lblHoriz);
        _cboRecLen.SelectedIndexChanged += async (_, _) =>
        {
            if (_syncRecLen || _cboRecLen.SelectedIndex < 0) return;
            int n = TekScope.RecordLengths[_cboRecLen.SelectedIndex];
            await RunOp(Loc.T($"레코드 길이 {n:N0}", $"Record length {n:N0}"), async () =>
            {
                var s = RequireScope();
                await Task.Run(() => s.SetRecordLength(n));
                await UpdateHorizontal();
            });
        };

        // --- menu bar: file / acquisition / view / language
        var menu = BuildMenu();
        MainMenuStrip = menu;

        // --- status bar: save folder
        var status = new StatusStrip();
        status.Items.AddRange([_stFolder, _stAutoSave]);
        _stFolder.Click += (_, _) => OpenFolder();
        _miAutoSave.CheckedChanged += (_, _) => { UpdateStatus(); SaveSettings(); };

        // --- center
        _tabScreen.L("화면", "Screen");
        _tabScreen.Controls.Add(_pic);
        _tabWave.Controls.Add(_wave);
        _tabs.TabPages.AddRange([_tabScreen, _tabWave]);

        // dock order: fill first, then edges (last added docks first)
        Controls.Add(_tabs);
        Controls.Add(left);
        Controls.Add(_log);
        Controls.Add(status);
        Controls.Add(top);
        Controls.Add(menu);

        _btnRefresh.Click += async (_, _) => await RunOp(Loc.T("장치 검색", "Device search"), RefreshResources);
        _btnConnect.Click += async (_, _) => await RunOp(_btnConnect.Text, ToggleConnect);

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
                await RunOp(Loc.T("파형 불러오기", "Load waveforms"), () => LoadCsv(f[0]));
        };

        LoadSettings();
        RecolorChannelChecks();
        _wave.LabelsChanged += (_, _) => SaveSettings();
        _wave.ColorsChanged += (_, _) => { RecolorChannelChecks(); SaveSettings(); };
        _wave.TicksChanged += (_, _) => SaveTicks();
        Activated += (_, _) => { ReloadExternalLabels(); ReloadExternalTicks(); };
        Loc.Changed += OnLanguageChanged;
        Disposed += (_, _) => Loc.Changed -= OnLanguageChanged;
        ApplyDynamicTexts();
        UpdateUi();
        Shown += async (_, _) =>
        {
            if (_testMode) return;
            await RunOp(Loc.T("장치 검색", "Device search"), RefreshResources);
            if (_cboRsrc.Text.StartsWith("USB", StringComparison.OrdinalIgnoreCase))
                await RunOp(Loc.T("연결", "Connect"), ToggleConnect);
        };
        FormClosing += (_, _) => { SaveSettings(); _scope?.Dispose(); };
    }

    // ---------------------------------------------------------------- language

    private static UiLang ReadLanguageSetting()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return Loc.Parse(JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Language);
        }
        catch { /* corrupt settings → default */ }
        return Loc.Default;
    }

    private void SetLanguage(UiLang lang)
    {
        if (lang == Loc.Lang) return;
        Loc.Set(lang);
        SaveSettings();
        Log(Loc.T("언어: 한국어", "Language: English"));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyDynamicTexts();

    /// <summary>Texts built from state (connection, folder, horizontal, tab name) in the current language.</summary>
    private void ApplyDynamicTexts()
    {
        foreach (ToolStripMenuItem mi in _miLang.DropDownItems) mi.Checked = (UiLang)mi.Tag! == Loc.Lang;
        if (_scope == null) _lblIdn.Text = Loc.T("연결 안 됨", "Not connected");
        _tabWave.Text = _lastCsv != null && _tabWaveFromFile ? Loc.T("파형", "Waveforms") + $" — {Path.GetFileName(_lastCsv)}" : Loc.T("파형", "Waveforms");
        ShowHorizontal();
        UpdateStatus();
        UpdateUi();
    }

    private bool _tabWaveFromFile;

    // ---------------------------------------------------------------- operations

    private TekScope RequireScope() => _scope ?? throw new InvalidOperationException(Loc.T("스코프가 연결되지 않았습니다.", "Scope is not connected."));
    private string Folder => string.IsNullOrWhiteSpace(_folder) ? DefaultFolder : _folder.Trim();
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
            Log(Loc.T($"{name} 완료 ({sw.ElapsedMilliseconds} ms)", $"{name}: done ({sw.ElapsedMilliseconds} ms)"));
        }
        catch (Exception ex)
        {
            Log(Loc.T($"{name} 실패: {ex.Message}", $"{name}: failed — {ex.Message}"));
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
        Log(Loc.T($"VISA 리소스 {list.Count}개: ", $"{list.Count} VISA resource(s): ") + string.Join(", ", list));
        if (!string.IsNullOrEmpty(prev) && list.Contains(prev)) _cboRsrc.Text = prev;
        else if (_cboRsrc.Items.Count > 0) _cboRsrc.SelectedIndex = 0;
    }

    private async Task ToggleConnect()
    {
        if (_scope != null)
        {
            _scope.Dispose();
            _scope = null;
            _lblIdn.Text = Loc.T("연결 안 됨", "Not connected");
            return;
        }
        string rsrc = _cboRsrc.Text.Trim();
        if (rsrc.Length == 0) throw new InvalidOperationException(Loc.T("장치를 선택하세요.", "Select a device."));
        var scope = await Task.Run(() => new TekScope(rsrc));
        _scope = scope;
        _lblIdn.Text = scope.Idn;
        Log(Loc.T("연결됨: ", "Connected: ") + scope.Idn);
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
        _lastHoriz = h;
        ShowHorizontal();
    }

    private void ShowHorizontal()
    {
        if (_lastHoriz is not { } h) { _lblHoriz.Text = ""; return; }
        _lblHoriz.Text = $"{Eng.Format(h.TimePerDiv, "s")}/div · {Eng.Format(h.SampleRate, "S/s")}\n" +
                         Loc.T("한 번 캡처 = ", "One capture = ") + $"{Eng.Format(h.Duration, "s")} ({h.RecordLength:N0} pts)";
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
        Log(Loc.T("화면 캡처: ", "Screen capture: ") + $"{png.Length / 1024.0:F1} KB");
        if (_miAutoSave.Checked) SavePng();
    }

    private async Task CaptureWaveforms()
    {
        var s = RequireScope();
        if (_chkAutoCh.Checked) await UpdateDisplayedChannels();
        await UpdateHorizontal();
        var sources = _chk.Where(c => c.Checked).Select(c => (string)c.Tag!).ToList();
        if (sources.Count == 0) throw new InvalidOperationException(Loc.T("선택된 채널이 없습니다 (스코프에 표시된 채널이 없음).", "No channel selected (no channel is displayed on the scope)."));
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
        _lastCsv = null; // not saved yet
        _wave.SetWaveforms(waves);
        _tabs.SelectedTab = _tabWave;
        _tabWaveFromFile = false;
        ApplyDynamicTexts();
        if (_miAutoSave.Checked) await SaveCsvAsync();
    }

    private async Task LoadCsvDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Title = Loc.T("파형 CSV 불러오기", "Open waveform CSV"),
            Filter = Loc.T("CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*", "CSV files (*.csv)|*.csv|All files (*.*)|*.*"),
            InitialDirectory = Directory.Exists(Folder) ? Folder : DefaultFolder,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await LoadCsv(dlg.FileName);
    }

    private async Task LoadCsv(string path)
    {
        Log(Loc.T("불러오는 중: ", "Loading: ") + $"{path} ({new FileInfo(path).Length / 1e6:F1} MB)");
        var progress = new Progress<string>(Log);
        var waves = await Task.Run(() => CsvImport.Read(path, progress));
        _lastWaves = waves;
        _lastCsv = path;
        _wave.SetWaveforms(waves, applySavedLabels: false);
        _tabs.SelectedTab = _tabWave;
        _tabWaveFromFile = true;
        ApplyDynamicTexts();
        foreach (var w in waves)
            Log($"  {w.DisplayName}: {w.Length:N0} pts, dt {Eng.Format(w.XIncr, w.XUnit)}, {Eng.Format(w.TStart, w.XUnit)} ~ {Eng.Format(w.TEnd, w.XUnit)}");
    }

    /// <param name="path">null = <c>&lt;folder&gt;\&lt;model&gt;_&lt;time&gt;_screen.png</c></param>
    private void SavePng(string? path = null)
    {
        if (_lastPng == null) throw new InvalidOperationException(Loc.T("저장할 화면 캡처가 없습니다.", "There is no screen capture to save."));
        path ??= Path.Combine(Folder, Stamp() + "_screen.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, _lastPng);
        Log(Loc.T("저장: ", "Saved: ") + path);
    }

    /// <param name="path">null = <c>&lt;folder&gt;\&lt;model&gt;_&lt;time&gt;_wave.csv</c></param>
    private async Task SaveCsvAsync(string? path = null)
    {
        var waves = _lastWaves ?? throw new InvalidOperationException(Loc.T("저장할 파형이 없습니다.", "There are no waveforms to save."));
        path ??= Path.Combine(Folder, Stamp() + "_wave.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Task.Run(() => CsvExport.Write(path, waves));
        Log(Loc.T("저장: ", "Saved: ") + path);
        if (ReferenceEquals(waves, _lastWaves)) { _lastCsv = path; SaveTicks(); }
    }

    /// <summary>Asks for a file name; returns null when cancelled.</summary>
    private string? AskSavePath(string title, string filter, string suffix)
    {
        using var dlg = new SaveFileDialog
        {
            Title = title, Filter = filter, OverwritePrompt = true,
            InitialDirectory = Directory.Exists(Folder) ? Folder : DefaultFolder,
            FileName = Stamp() + suffix,
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    private async Task SaveCsvAs()
    {
        if (_lastWaves == null) throw new InvalidOperationException(Loc.T("저장할 파형이 없습니다.", "There are no waveforms to save."));
        if (AskSavePath(Loc.T("파형 CSV 저장", "Save waveform CSV"), Loc.T("CSV 파일 (*.csv)|*.csv", "CSV files (*.csv)|*.csv"), "_wave.csv") is { } p) await SaveCsvAsync(p);
    }

    private Task SavePngAs()
    {
        if (_lastPng == null) throw new InvalidOperationException(Loc.T("저장할 화면 캡처가 없습니다.", "There is no screen capture to save."));
        if (AskSavePath(Loc.T("화면 PNG 저장", "Save screen PNG"), Loc.T("PNG 이미지 (*.png)|*.png", "PNG images (*.png)|*.png"), "_screen.png") is { } p) SavePng(p);
        return Task.CompletedTask;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Folder, UseDescriptionForTitle = true, Description = Loc.T("저장 폴더 선택", "Choose save folder") };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _folder = dlg.SelectedPath;
        UpdateStatus();
        SaveSettings();
        Log(Loc.T("저장 폴더: ", "Save folder: ") + Folder);
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start("explorer.exe", Folder);
    }

    /// <summary>Headless UI check: TekCapture.exe --uitest &lt;csv&gt; &lt;outdir&gt;. Loads the CSV, recolors CH2,
    /// renders the window to main.png and lists the menus in menu.txt. Never writes settings.json.</summary>
    internal static int UiTest(string csv, string outDir, string? lang = null)
    {
        Directory.CreateDirectory(outDir);
        using var f = new MainForm { Size = new Size(1400, 900) };
        f._loadingSettings = true; // suppress SaveSettings for the whole test
        if (lang != null) Loc.Set(Loc.Parse(lang));
        string sfx = "_" + Loc.Code;
        var waves = CsvImport.Read(csv);
        f._lastWaves = waves;
        f._lastCsv = csv;
        f._wave.SetWaveforms(waves, applySavedLabels: false);
        f._tabWaveFromFile = true;
        f.ApplyDynamicTexts();
        f._tabs.SelectedTab = f._tabWave;
        f._wave.SetChannelColor(waves[Math.Min(1, waves.Count - 1)].Source, Color.FromArgb(255, 128, 0));
        f.UpdateUi();
        f._testMode = true;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.ShowInTaskbar = false;
        f.Show();
        Application.DoEvents();
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
            bmp.Save(Path.Combine(outDir, $"main{sfx}.png"));
        }
        using var w = new StreamWriter(Path.Combine(outDir, $"menu{sfx}.txt"));
        f.BuildColorMenu();
        void Dump(ToolStripItemCollection items, int depth)
        {
            foreach (ToolStripItem it in items)
            {
                string keys = it is ToolStripMenuItem m && m.ShortcutKeys != Keys.None ? $"  [{m.ShortcutKeys}]" : "";
                string state = it is ToolStripMenuItem m2 ? (m2.CheckOnClick ? (m2.Checked ? " ☑" : " ☐") : "") + (m2.Enabled ? "" : " (disabled)") : "";
                w.WriteLine(new string(' ', depth * 2) + (it is ToolStripSeparator ? "────" : it.Text + keys + state));
                if (it is ToolStripMenuItem mi) Dump(mi.DropDownItems, depth + 1);
            }
        }
        Dump(f.MainMenuStrip!.Items, 0);
        w.WriteLine($"colors: {string.Join(", ", WaveformView.CustomColors.Select(kv => $"{kv.Key}={WaveformView.ToHex(kv.Value)}"))}");
        return 0;
    }

    // ---------------------------------------------------------------- menu

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        static string Clean(string t) => t.Replace("(&", "(").Replace("&", "").Replace("…", "").Replace("()", "").Trim();

        ToolStripMenuItem Item(string ko, string en, Func<Task> op, Keys keys = Keys.None, bool needsScope = false)
        {
            var mi = new ToolStripMenuItem { ShortcutKeys = keys }.L(ko, en);
            mi.Click += async (_, _) => await RunOp(Clean(mi.Text ?? ""), op);
            if (needsScope) _scopeItems.Add(mi);
            return mi;
        }
        ToolStripMenuItem Act(string ko, string en, Action a, Keys keys = Keys.None)
        {
            var mi = new ToolStripMenuItem { ShortcutKeys = keys }.L(ko, en);
            mi.Click += (_, _) =>
            {
                try { a(); }
                catch (Exception ex) { Log(Clean(mi.Text ?? "") + Loc.T(" 실패: ", ": failed — ") + ex.Message); }
            };
            return mi;
        }

        var file = new ToolStripMenuItem().L("파일(&F)", "&File");
        file.DropDownItems.AddRange(
        [
            Item("파형 CSV 열기(&O)…", "&Open waveform CSV…", LoadCsvDialog, Keys.Control | Keys.O),
            new ToolStripSeparator(),
            Item("파형 CSV 저장(&S)", "&Save waveform CSV", () => SaveCsvAsync(), Keys.Control | Keys.S),
            Item("파형 CSV 다른 이름으로 저장…", "Save waveform CSV &as…", SaveCsvAs),
            Item("화면 PNG 저장(&P)", "Save screen &PNG", () => { SavePng(); return Task.CompletedTask; }, Keys.Control | Keys.Shift | Keys.S),
            Item("화면 PNG 다른 이름으로 저장…", "Save screen PNG as…", SavePngAs),
            new ToolStripSeparator(),
            _miAutoSave,
            Act("저장 폴더 변경(&D)…", "Change save fol&der…", BrowseFolder),
            Act("저장 폴더 열기(&E)", "Op&en save folder", OpenFolder),
            new ToolStripSeparator(),
            Act("끝내기(&X)", "E&xit", Close),
        ]);

        var acq = new ToolStripMenuItem().L("수집(&A)", "&Acquire");
        acq.DropDownItems.AddRange(
        [
            Item("Run(&R)", "&Run", () => Task.Run(() => RequireScope().Run()), Keys.F5, true),
            Item("Stop(&T)", "S&top", () => Task.Run(() => RequireScope().Stop()), Keys.F6, true),
            Item("Single(&G)", "Sin&gle", () => Task.Run(() => RequireScope().Single()), Keys.F7, true),
            new ToolStripSeparator(),
            Item("화면 캡처 (PNG)(&C)", "&Capture screen (PNG)", CaptureScreen, Keys.F8, true),
            Item("파형 캡처 (데이터)(&W)", "Capture &waveforms (data)", CaptureWaveforms, Keys.F9, true),
            Item("화면 + 파형 모두(&B)", "Screen + waveforms (&B)", async () => { await CaptureScreen(); await CaptureWaveforms(); }, Keys.Control | Keys.F9, true),
        ]);

        var view = new ToolStripMenuItem().L("보기(&V)", "&View");
        _miColors.DropDownItems.Add(new ToolStripMenuItem("-") { Enabled = false });
        _miColors.DropDownOpening += (_, _) => BuildColorMenu();
        view.DropDownItems.AddRange(
        [
            _miColors,
            new ToolStripSeparator(),
            Act("화면 탭(&1)", "&1 Screen tab", () => _tabs.SelectedTab = _tabScreen, Keys.Control | Keys.D1),
            Act("파형 탭(&2)", "&2 Waveform tab", () => _tabs.SelectedTab = _tabWave, Keys.Control | Keys.D2),
        ]);

        // Language: always labelled in English too, so it can be found in either language
        foreach (var (lang, text) in new[] { (UiLang.Ko, "한국어 (Korean)"), (UiLang.En, "English") })
        {
            var mi = new ToolStripMenuItem(text) { Tag = lang };
            mi.Click += (_, _) => SetLanguage(lang);
            _miLang.DropDownItems.Add(mi);
        }

        menu.Items.AddRange([file, acq, view, _miLang]);
        return menu;
    }

    /// <summary>One entry per channel (shown + checked channels), with its current color as the icon.</summary>
    private void BuildColorMenu()
    {
        foreach (ToolStripItem it in _miColors.DropDownItems) it.Image?.Dispose();
        _miColors.DropDownItems.Clear();

        var sources = _wave.View.Channels.Select(c => (c.Source, c.DisplayName)).ToList();
        foreach (var c in _chk)
        {
            var s = (string)c.Tag!;
            if (!sources.Any(x => x.Source.Equals(s, StringComparison.OrdinalIgnoreCase))) sources.Add((s, s));
        }
        foreach (var (src, name) in sources)
        {
            bool custom = WaveformView.CustomColors.ContainsKey(src);
            var mi = new ToolStripMenuItem(name + (custom ? "" : Loc.T("  (기본)", "  (default)")) + "…", Swatch(WaveformView.ColorOf(src)));
            mi.Click += (_, _) => _wave.PickColor(src);
            _miColors.DropDownItems.Add(mi);
        }
        _miColors.DropDownItems.Add(new ToolStripSeparator());
        var reset = new ToolStripMenuItem(Loc.T("모든 색상을 기본값으로", "Reset all colors to default"), null, (_, _) => _wave.ResetAllColors())
        {
            Enabled = WaveformView.CustomColors.Count > 0,
        };
        _miColors.DropDownItems.Add(reset);
    }

    private static Bitmap Swatch(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using (var b = new SolidBrush(c)) g.FillRectangle(b, 1, 1, 14, 14);
        g.DrawRectangle(Pens.DimGray, 1, 1, 13, 13);
        return bmp;
    }

    private void RecolorChannelChecks()
    {
        foreach (var c in _chk) c.BackColor = WaveformView.ColorOf((string)c.Tag!);
    }

    private void UpdateStatus()
    {
        _stFolder.Text = Loc.T("저장 폴더: ", "Save folder: ") + Folder;
        _stFolder.ToolTipText = Loc.T("클릭하면 폴더를 엽니다. 변경: 파일 → 저장 폴더 변경", "Click to open the folder. Change: File → Change save folder");
        _stAutoSave.Text = _miAutoSave.Checked ? Loc.T("자동 저장 켜짐", "Auto-save on") : Loc.T("자동 저장 꺼짐", "Auto-save off");
    }

    // ---------------------------------------------------------------- ui helpers

    private void UpdateUi()
    {
        bool connected = _scope != null;
        foreach (var b in _scopeButtons) b.Enabled = connected && !_busy;
        foreach (var mi in _scopeItems) mi.Enabled = connected && !_busy;
        _btnConnect.Text = connected ? Loc.T("연결 해제", "Disconnect") : Loc.T("연결", "Connect");
        _btnConnect.Enabled = !_busy;
        _btnRefresh.Enabled = !_busy && !connected;
        _cboRsrc.Enabled = !connected;
    }

    private void Log(string msg)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    private bool _loadingSettings;
    private bool _testMode;

    private void LoadSettings()
    {
        _folder = DefaultFolder;
        _loadingSettings = true;
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var st = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (st == null) return;
            if (!string.IsNullOrWhiteSpace(st.Folder)) _folder = st.Folder;
            if (!string.IsNullOrWhiteSpace(st.Resource)) _cboRsrc.Text = st.Resource;
            _chkInk.Checked = st.InkSaver;
            _miAutoSave.Checked = st.AutoSave;
            _chkStopFirst.Checked = st.StopFirst;
            if (st.Labels != null) _wave.SavedLabels = new(st.Labels, StringComparer.OrdinalIgnoreCase);
            WaveformView.CustomColors.Clear();
            foreach (var (src, hex) in st.Colors ?? [])
                if (WaveformView.TryParseHex(hex, out var col)) WaveformView.CustomColors[src] = col;
            _settingsStamp = File.GetLastWriteTimeUtc(SettingsPath);
        }
        catch { /* ignore corrupt settings */ }
        finally { _loadingSettings = false; }
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var colors = WaveformView.CustomColors.ToDictionary(kv => kv.Key.ToUpperInvariant(), kv => WaveformView.ToHex(kv.Value));
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(_folder, _cboRsrc.Text, _chkInk.Checked, _miAutoSave.Checked, _chkStopFirst.Checked, _wave.SavedLabels, colors, Loc.Code)));
            _settingsStamp = File.GetLastWriteTimeUtc(SettingsPath);
        }
        catch { }
    }

    /// <summary>Last settings.json write time seen by this app. A newer file means another program
    /// (TekMcp's set_channel_labels) changed the labels.</summary>
    private DateTime _settingsStamp;

    /// <summary>Publishes tick A/B (and which CSV they refer to) for TekMcp's measure_ticks.</summary>
    private void SaveTicks()
    {
        if (_applyingExternalTicks) return;
        var v = _wave.View;
        _ticksWritten = DateTime.Now;
        new TickFile(v.TickA, v.TickB, _lastCsv, _ticksWritten).Save();
    }

    private DateTime _ticksWritten;
    private bool _applyingExternalTicks;

    /// <summary>Shows ticks placed by TekMcp's measure_ticks when they refer to the CSV shown here.</summary>
    private void ReloadExternalTicks()
    {
        var t = TickFile.Load();
        if (t == null || t.Updated <= _ticksWritten || t.Source == null || _lastCsv == null) return;
        if (!string.Equals(Path.GetFullPath(t.Source), Path.GetFullPath(_lastCsv), StringComparison.OrdinalIgnoreCase)) return;
        _ticksWritten = t.Updated;
        _applyingExternalTicks = true;
        try
        {
            _wave.View.SetTick('A', t.A);
            _wave.View.SetTick('B', t.B);
        }
        finally { _applyingExternalTicks = false; }
        Log(Loc.T("MCP에서 설정한 틱을 표시: ", "Showing ticks set by MCP: ") + $"A {t.A?.ToString("G6") ?? "-"}, B {t.B?.ToString("G6") ?? "-"}");
    }

    private void ReloadExternalLabels()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var t = File.GetLastWriteTimeUtc(SettingsPath);
            if (t == _settingsStamp) return;
            _settingsStamp = t;
            var st = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (st?.Labels is { Count: > 0 } labels && _wave.MergeExternalLabels(labels))
            {
                Log(Loc.T("라벨이 외부(MCP)에서 변경되어 다시 읽었습니다: ", "Labels were changed outside the app (MCP) and reloaded: ") + string.Join(", ", labels.Select(kv => $"{kv.Key}={kv.Value}")));
            }
        }
        catch { /* file being written or corrupt → try again on next activation */ }
    }
}
