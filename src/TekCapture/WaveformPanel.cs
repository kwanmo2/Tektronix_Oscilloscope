namespace TekCapture;

/// <summary>Waveform viewer + side control panel (time base, per-channel scale/position/label).</summary>
public sealed class WaveformPanel : UserControl
{
    public WaveformView View { get; } = new() { Dock = DockStyle.Fill };

    /// <summary>Labels the user typed, by source ("CH1" → "VIN"). Overrides the scope's own label.</summary>
    public Dictionary<string, string> SavedLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public event EventHandler? LabelsChanged;

    private readonly HScrollBar _scroll = new() { Dock = DockStyle.Bottom, Height = 18 };
    private readonly FlowLayoutPanel _side = new()
    {
        Dock = DockStyle.Right, Width = 300, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6, 4, 6, 4),
    };
    private readonly FlowLayoutPanel _chHost = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
    private readonly TextBox _txtTdiv = new() { Width = 100 };
    private readonly TextBox _txtCenter = new() { Width = 100 };
    private readonly Label _lblInfo = new() { AutoSize = true, MaximumSize = new Size(260, 0), Margin = new Padding(0, 4, 0, 2) };
    private readonly TextBox _txtTickA = new() { Width = 110 };
    private readonly TextBox _txtTickB = new() { Width = 110 };
    private readonly Label _lblTicks = new() { AutoSize = true, MaximumSize = new Size(262, 0), Margin = new Padding(0, 4, 0, 2), Font = new Font("Consolas", 8.5f) };
    private readonly ToolTip _tip = new();

    /// <summary>Raised after tick A/B changed (for saving them for TekMcp).</summary>
    public event EventHandler? TicksChanged;

    private void UpdateTicks()
    {
        string xu = View.HasData ? View.Channels[0].W.XUnit : "s";
        _txtTickA.Text = View.TickA is double a ? Eng.Format(a, xu) : "";
        _txtTickB.Text = View.TickB is double b ? Eng.Format(b, xu) : "";
        _txtTickA.Modified = _txtTickB.Modified = false;
        _lblTicks.Text = View.HasData
            ? TickMeasure.Readout(View.Channels.Select(c => c.W).ToList(), View.TickA, View.TickB)
            : "";
        TicksChanged?.Invoke(this, EventArgs.Empty);
    }
    private readonly List<ChannelRow> _rows = [];
    private bool _sync;

    private sealed class ChannelRow
    {
        public required ChannelState C;
        public required GroupBox Box;
        public required CheckBox Vis;
        public required Button Swatch;
        public required TextBox TxtLabel;
        public required TextBox TxtV;
        public required NumericUpDown NumPos;
        public required Label Stats;
    }

    public WaveformPanel()
    {
        var plotHost = new Panel { Dock = DockStyle.Fill };
        plotHost.Controls.Add(View);
        plotHost.Controls.Add(_scroll);
        Controls.Add(plotHost);
        Controls.Add(_side);

        // --- horizontal
        var hBox = Group("").L("수평 (시간축)", "Horizontal (time)");
        var hFlow = (FlowLayoutPanel)hBox.Controls[0];
        hFlow.Controls.Add(Row(Lbl("Time/div"),
            Small("▼", (_, _) => View.SetTimePerDiv(Nice.Step(View.TPerDiv, -1)), "작게 (확대)", "Smaller (zoom in)"),
            _txtTdiv,
            Small("▲", (_, _) => View.SetTimePerDiv(Nice.Step(View.TPerDiv, 1)), "크게 (축소)", "Larger (zoom out)")));
        hFlow.Controls.Add(Row(Lbl("").L("중심 시간", "Center"),
            Small("◀", (_, _) => View.Pan(-1), "1 div 왼쪽으로", "1 div left"),
            _txtCenter,
            Small("▶", (_, _) => View.Pan(1), "1 div 오른쪽으로", "1 div right")));
        hFlow.Controls.Add(_lblInfo);
        hFlow.Controls.Add(Row(Wide("전체 보기", "Fit all", (_, _) => View.FitTime(), 120)));
        _side.Controls.Add(hBox);

        // --- ticks A/B (time cursors)
        var tBox = Group("").L("틱 A / B (시간 커서)", "Ticks A / B (time cursors)");
        var tFlow = (FlowLayoutPanel)tBox.Controls[0];
        var lblA = Lbl("A"); lblA.ForeColor = WaveformView.TickAColor; lblA.Font = new Font(Font, FontStyle.Bold);
        var lblB = Lbl("B"); lblB.ForeColor = WaveformView.TickBColor; lblB.Font = new Font(Font, FontStyle.Bold);
        tFlow.Controls.Add(Row(lblA, _txtTickA, Wide("중심", "Center", (_, _) => View.SetTick('A', View.TCenter), 56)));
        tFlow.Controls.Add(Row(lblB, _txtTickB, Wide("중심", "Center", (_, _) => View.SetTick('B', View.TCenter), 56)));
        tFlow.Controls.Add(Row(
            Wide("A↔B 교환", "Swap A↔B", (_, _) => { var a = View.TickA; View.SetTick('A', View.TickB); View.SetTick('B', a); }, 80),
            Wide("A–B 확대", "Zoom A–B", (_, _) => View.ZoomToTicks(), 80),
            Wide("지우기", "Clear", (_, _) => View.ClearTicks(), 70)));
        tFlow.Controls.Add(_lblTicks);
        tFlow.Controls.Add(Row(Wide("측정값 복사", "Copy readout", (_, _) => { if (_lblTicks.Text.Length > 0) Clipboard.SetText(_lblTicks.Text); }, 100)));
        _side.Controls.Add(tBox);
        OnCommit(_txtTickA, s => View.SetTick('A', s.Trim().Length == 0 ? null : Eng.TryParse(s, "s", out var v) ? v : View.TickA));
        OnCommit(_txtTickB, s => View.SetTick('B', s.Trim().Length == 0 ? null : Eng.TryParse(s, "s", out var v) ? v : View.TickB));
        _tip.Tip(_txtTickA, "예: -12.5u, 3m — Enter로 적용. 비우면 지움", "e.g. -12.5u, 3m — Enter to apply. Empty = clear");
        _tip.Tip(_txtTickB, "예: -12.5u, 3m — Enter로 적용. 비우면 지움", "e.g. -12.5u, 3m — Enter to apply. Empty = clear");
        View.TicksChanged += (_, _) => UpdateTicks();

        OnCommit(_txtTdiv, s => { if (Eng.TryParse(s, "s", out var v) && v > 0) View.SetTimePerDiv(v); });
        OnCommit(_txtCenter, s => { if (Eng.TryParse(s, "s", out var v)) View.SetCenter(v); });
        _tip.Tip(_txtTdiv, "예: 10u, 500n, 2ms — Enter로 적용", "e.g. 10u, 500n, 2ms — Enter to apply");
        _tip.Tip(_txtCenter, "예: 0, -25u, 1.5m — Enter로 적용", "e.g. 0, -25u, 1.5m — Enter to apply");

        // --- all channels
        var aBox = Group("").L("전체 채널", "All channels");
        var aFlow = (FlowLayoutPanel)aBox.Controls[0];
        aFlow.Controls.Add(Row(Wide("스코프 설정", "Scope setup", (_, _) => View.ResetAllToScope(), 85),
                               Wide("모두 자동", "Auto all", (_, _) => View.AutoScaleAll(), 80),
                               Wide("분리 배치", "Stack", (_, _) => View.StackChannels(), 80)));
        _tip.Tip(aBox, "분리 배치: 채널마다 화면 띠를 나눠서 겹치지 않게 배치", "Stack: give each channel its own band so traces don't overlap");
        _side.Controls.Add(aBox);

        _side.Controls.Add(_chHost);

        _side.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(270, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 0),
        }.L("마우스 조작\n" +
            "· 휠: 시간 확대/축소 (커서 위치 기준)\n" +
            "· 드래그: 좌우 이동\n" +
            "· 우클릭 드래그: 선택 구간 확대\n" +
            "· 더블클릭 / Home: 전체 보기\n" +
            "· 왼쪽 채널 마커 드래그: 위치 이동\n" +
            "· Shift+휠: 선택 채널 V/div\n" +
            "· Ctrl+휠, ↑↓: 선택 채널 위치\n" +
            "· ←→: 1 div 이동, +/−: Time/div\n" +
            "· 틱: A / B 키 (마우스 위치), Ctrl+클릭 = A, Ctrl+우클릭 = B\n" +
            "  Shift를 함께 누르면 선택 채널의 가장 가까운 엣지에 맞춤\n" +
            "  틱 선 드래그로 이동, Esc로 모두 지움",
            "Mouse\n" +
            "· Wheel: zoom time (around the cursor)\n" +
            "· Drag: pan left/right\n" +
            "· Right-drag: zoom to selection\n" +
            "· Double-click / Home: fit all\n" +
            "· Drag channel marker (left): move position\n" +
            "· Shift+wheel: V/div of selected channel\n" +
            "· Ctrl+wheel, ↑↓: position of selected channel\n" +
            "· ←→: pan 1 div, +/−: Time/div\n" +
            "· Ticks: A / B key (at mouse), Ctrl+click = A, Ctrl+right-click = B\n" +
            "  Hold Shift to snap to the nearest edge of the selected channel\n" +
            "  Drag a tick line to move it, Esc clears both"));

        _scroll.ValueChanged += (_, _) =>
        {
            if (_sync || !View.HasData) return;
            double span = View.T1 - View.T0;
            double t0 = View.FullT0 + _scroll.Value / 10000.0 * Math.Max(0, View.FullSpan - span);
            View.SetCenter(t0 + span / 2);
        };
        View.ViewChanged += (_, _) => SyncFromView();
        View.SelectionChanged += (_, _) => SyncSelection();
        Loc.Changed += OnLanguageChanged;
        Disposed += (_, _) => Loc.Changed -= OnLanguageChanged;
        SyncFromView();
    }

    /// <summary>Rebuilds the texts computed from state (channel rows, readouts) in the new language.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildRows();
        SyncFromView();
        SyncSelection();
        UpdateTicks();
        View.Invalidate();
    }

    /// <param name="applySavedLabels">false for files loaded from disk: keep the labels stored in the file.</param>
    public void SetWaveforms(List<Waveform> waves, bool applySavedLabels = true)
    {
        _labelsFromFile = !applySavedLabels;
        if (applySavedLabels)
            foreach (var w in waves)
                if (SavedLabels.TryGetValue(w.Source, out var lbl)) w.Label = lbl;
        View.SetWaveforms(waves);
        RebuildRows();
        SyncFromView();
        SyncSelection();
    }

    private bool _labelsFromFile;

    /// <summary>Merges labels changed outside the app (e.g. by TekMcp) and shows them on a live capture.
    /// Waveforms loaded from a file keep the labels stored in the file.</summary>
    public bool MergeExternalLabels(IReadOnlyDictionary<string, string> labels)
    {
        bool changed = false;
        foreach (var (k, v) in labels)
            if (!SavedLabels.TryGetValue(k, out var cur) || cur != v) { SavedLabels[k] = v; changed = true; }
        if (!changed || _labelsFromFile || !View.HasData) return changed;
        foreach (var c in View.Channels)
            if (SavedLabels.TryGetValue(c.Source, out var l)) c.Label = l;
        SyncFromView();
        View.Changed();
        UpdateTicks();
        return true;
    }

    // ------------------------------------------------------------------ colors

    /// <summary>Raised after a trace color changed (for saving settings and recoloring other controls).</summary>
    public event EventHandler? ColorsChanged;

    /// <summary>Opens a color dialog for <paramref name="source"/>. Returns true when a color was chosen.</summary>
    public bool PickColor(string source)
    {
        using var dlg = new ColorDialog
        {
            Color = WaveformView.ColorOf(source),
            FullOpen = true,
            AnyColor = true,
            CustomColors = WaveformView.Palette.Select(p => p.R | p.G << 8 | p.B << 16).Take(16).ToArray(),
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return false;
        SetChannelColor(source, dlg.Color);
        return true;
    }

    /// <summary>Sets (or with null, resets to the default) the trace color of a source.</summary>
    public void SetChannelColor(string source, Color? color)
    {
        if (color is Color c) WaveformView.CustomColors[source] = Color.FromArgb(c.R, c.G, c.B);
        else WaveformView.CustomColors.Remove(source);
        RefreshColors();
        ColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetAllColors()
    {
        WaveformView.CustomColors.Clear();
        RefreshColors();
        ColorsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshColors()
    {
        foreach (var r in _rows) r.Vis.BackColor = r.Swatch.BackColor = r.C.Color;
        View.Invalidate();
    }

    // ------------------------------------------------------------------ channel rows

    private void RebuildRows()
    {
        _chHost.SuspendLayout();
        foreach (Control c in _chHost.Controls) c.Dispose();
        _chHost.Controls.Clear();
        _rows.Clear();

        foreach (var c in View.Channels)
        {
            var box = Group("");
            var flow = (FlowLayoutPanel)box.Controls[0];
            var vis = new CheckBox
            {
                Text = c.Source, Checked = c.Visible, AutoSize = true, BackColor = c.Color, ForeColor = Color.Black,
                Font = new Font(Font, FontStyle.Bold), Padding = new Padding(2, 0, 4, 0), Margin = new Padding(0, 4, 6, 0),
            };
            var txtLabel = new TextBox { Width = 146, PlaceholderText = Loc.T("라벨 (예: VIN, PWM)", "Label (e.g. VIN, PWM)"), Text = c.Label };
            var txtV = new TextBox { Width = 100 };
            var numPos = new NumericUpDown { Width = 100, DecimalPlaces = 2, Increment = 0.1m, Minimum = -100, Maximum = 100 };
            var stats = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 2, 0, 0) };
            var swatch = new Button { Width = 24, Height = 23, BackColor = c.Color, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 1, 4, 0), Cursor = Cursors.Hand };
            swatch.FlatAppearance.BorderColor = Color.DimGray;
            swatch.Click += (_, _) => { View.Selected = c; PickColor(c.Source); };
            var swMenu = new ContextMenuStrip();
            swMenu.Items.Add(Loc.T("색상 변경…", "Change color…"), null, (_, _) => PickColor(c.Source));
            swMenu.Items.Add(Loc.T("기본 색상으로", "Reset to default"), null, (_, _) => SetChannelColor(c.Source, null));
            swatch.ContextMenuStrip = swMenu;
            _tip.Tip(swatch, "파형 색상 변경 (우클릭: 기본 색상으로)", "Change trace color (right-click: reset to default)");
            var row = new ChannelRow { C = c, Box = box, Vis = vis, Swatch = swatch, TxtLabel = txtLabel, TxtV = txtV, NumPos = numPos, Stats = stats };

            flow.Controls.Add(Row(vis, swatch, txtLabel));
            flow.Controls.Add(Row(Lbl("V/div"),
                Small("▼", (_, _) => { View.Selected = c; View.StepVPerDiv(c, -1); }, "작게 (확대)", "Smaller (zoom in)"),
                txtV,
                Small("▲", (_, _) => { View.Selected = c; View.StepVPerDiv(c, 1); }, "크게 (축소)", "Larger (zoom out)")));
            flow.Controls.Add(Row(Lbl("").L("위치 (div)", "Pos (div)"), numPos));
            flow.Controls.Add(Row(
                Wide("자동", "Auto", (_, _) => { View.Selected = c; WaveformView.AutoScale(c); View.Changed(); }, 60),
                Wide("스코프 설정", "Scope setup", (_, _) => { View.Selected = c; if (!WaveformView.ResetToScope(c)) WaveformView.AutoScale(c); View.Changed(); }, 90)));
            flow.Controls.Add(stats);

            stats.Text = $"Min {Eng.Format(c.W.Min, c.Unit)} · Max {Eng.Format(c.W.Max, c.Unit)}\n" +
                         $"Vpp {Eng.Format(c.W.Max - c.W.Min, c.Unit)} · Mean {Eng.Format(c.W.Mean, c.Unit)}" +
                         (Math.Abs(c.OffsetV) > 0 ? "\n" + Loc.T("스코프 오프셋 ", "Scope offset ") + Eng.Format(c.OffsetV, c.Unit) : "");

            vis.CheckedChanged += (_, _) => { if (_sync) return; c.Visible = vis.Checked; View.Changed(); };
            OnCommit(txtV, s =>
            {
                if (Eng.TryParse(s, c.Unit, out var v) && v > 0)
                {
                    c.VPerDiv = Math.Clamp(v, WaveformView.MinVPerDiv, WaveformView.MaxVPerDiv);
                    View.Changed();
                }
            });
            OnCommit(txtLabel, s =>
            {
                if (s.Trim() == c.Label) return;
                c.Label = s;
                SavedLabels[c.Source] = c.Label;
                LabelsChanged?.Invoke(this, EventArgs.Empty);
                View.Changed();
                UpdateTicks(); // readout shows labels
            }, keepText: true);
            numPos.ValueChanged += (_, _) => { if (_sync) return; c.PosDiv = (double)numPos.Value; View.Changed(); };
            _tip.Tip(txtV, "예: 500m, 2, 100mV — Enter로 적용", "e.g. 500m, 2, 100mV — Enter to apply");
            _tip.Tip(txtLabel, "그래프·값 표시·CSV 헤더에 쓰입니다. 채널별로 기억됩니다.", "Shown in the plot, readouts and CSV header. Remembered per channel.");

            // clicking anywhere in the group selects the channel
            box.Enter += (_, _) => View.Selected = c;
            box.MouseDown += (_, _) => View.Selected = c;
            flow.MouseDown += (_, _) => View.Selected = c;

            _rows.Add(row);
            _chHost.Controls.Add(box);
        }
        _chHost.ResumeLayout();
    }

    // ------------------------------------------------------------------ sync

    private void SyncFromView()
    {
        _sync = true;
        try
        {
            bool has = View.HasData;
            _txtTdiv.Enabled = _txtCenter.Enabled = has;
            if (!has)
            {
                _lblInfo.Text = Loc.T("파형 없음", "No waveforms");
                _scroll.Enabled = false;
                return;
            }
            string xu = View.Channels[0].W.XUnit;
            if (!_txtTdiv.Modified) _txtTdiv.Text = Eng.Format(View.TPerDiv, xu);
            if (!_txtCenter.Modified) _txtCenter.Text = Eng.Format(View.TCenter, xu);
            int total = View.Channels.Max(c => c.W.Length);
            _lblInfo.Text =
                Loc.T("샘플 간격 ", "Sample interval ") + $"{Eng.Format(View.MinDx, xu)} ({Eng.Format(1 / View.MinDx, "S/s")})\n" +
                Loc.T("표시 ", "View ") + $"{Eng.Format(View.T0, xu)} ~ {Eng.Format(View.T1, xu)}\n" +
                Loc.T($"화면 {View.PointsInView():N0} / 전체 {total:N0} pts", $"On screen {View.PointsInView():N0} / total {total:N0} pts") + "\n" +
                Loc.T("전체 길이 ", "Total span ") + Eng.Format(View.FullSpan, xu);

            double span = View.T1 - View.T0, full = View.FullSpan;
            if (span >= full * 0.999)
            {
                _scroll.Enabled = false;
            }
            else
            {
                _scroll.Enabled = true;
                int large = (int)Math.Clamp(span / full * 10000, 1, 10000);
                _scroll.Minimum = 0;
                _scroll.Maximum = 10000 + large - 1;
                _scroll.LargeChange = large;
                _scroll.SmallChange = Math.Max(1, large / 10);
                _scroll.Value = (int)Math.Clamp((View.T0 - View.FullT0) / (full - span) * 10000, 0, 10000);
            }

            foreach (var r in _rows)
            {
                r.Vis.Checked = r.C.Visible;
                if (!r.TxtV.Modified) r.TxtV.Text = Eng.Format(r.C.VPerDiv, r.C.Unit);
                r.NumPos.Value = (decimal)Math.Clamp(Math.Round(r.C.PosDiv, 2), -100, 100);
                if (!r.TxtLabel.Focused) r.TxtLabel.Text = r.C.Label;
            }
        }
        finally { _sync = false; }
    }

    private void SyncSelection()
    {
        foreach (var r in _rows)
        {
            bool sel = r.C == View.Selected;
            r.Box.BackColor = sel ? Color.FromArgb(225, 236, 255) : SystemColors.Control;
            r.Box.Text = sel ? Loc.T("▶ 선택됨", "▶ Selected") : "";
        }
    }

    // ------------------------------------------------------------------ ui helpers

    private void OnCommit(TextBox t, Action<string> commit, bool keepText = false)
    {
        void Do()
        {
            if (_sync) return;
            commit(t.Text);
            t.Modified = false;
            if (!keepText) SyncFromView();
        }
        t.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Do(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { t.Modified = false; SyncFromView(); e.SuppressKeyPress = true; }
        };
        t.Leave += (_, _) => { if (t.Modified) Do(); };
    }

    private static GroupBox Group(string title)
    {
        var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 270, Padding = new Padding(6, 4, 6, 6), Margin = new Padding(0, 0, 0, 6) };
        box.Controls.Add(new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) });
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] cs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 1, 0, 1) };
        p.Controls.AddRange(cs);
        return p;
    }

    private static Label Lbl(string t) => new() { Text = t, Width = 66, Height = 23, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 2, 2, 0) };

    private Button Small(string t, EventHandler h, string tipKo, string tipEn)
    {
        var b = new Button { Text = t, Width = 28, Height = 25, Margin = new Padding(1), FlatStyle = FlatStyle.System };
        b.Click += h;
        _tip.Tip(b, tipKo, tipEn);
        return b;
    }

    private static Button Wide(string ko, string en, EventHandler h, int w)
    {
        var b = new Button { Width = w, Height = 27, Margin = new Padding(1) }.L(ko, en);
        b.Click += h;
        return b;
    }
}
