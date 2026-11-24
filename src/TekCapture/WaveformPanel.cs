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
    private readonly ToolTip _tip = new();
    private readonly List<ChannelRow> _rows = [];
    private bool _sync;

    private sealed class ChannelRow
    {
        public required ChannelState C;
        public required GroupBox Box;
        public required CheckBox Vis;
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
        var hBox = Group("수평 (시간축)");
        var hFlow = (FlowLayoutPanel)hBox.Controls[0];
        hFlow.Controls.Add(Row(Lbl("Time/div"),
            Small("▼", (_, _) => View.SetTimePerDiv(Nice.Step(View.TPerDiv, -1)), "작게 (확대)"),
            _txtTdiv,
            Small("▲", (_, _) => View.SetTimePerDiv(Nice.Step(View.TPerDiv, 1)), "크게 (축소)")));
        hFlow.Controls.Add(Row(Lbl("중심 시간"),
            Small("◀", (_, _) => View.Pan(-1), "1 div 왼쪽으로"),
            _txtCenter,
            Small("▶", (_, _) => View.Pan(1), "1 div 오른쪽으로")));
        hFlow.Controls.Add(_lblInfo);
        hFlow.Controls.Add(Row(Wide("전체 보기", (_, _) => View.FitTime(), 120)));
        _side.Controls.Add(hBox);

        OnCommit(_txtTdiv, s => { if (Eng.TryParse(s, "s", out var v) && v > 0) View.SetTimePerDiv(v); });
        OnCommit(_txtCenter, s => { if (Eng.TryParse(s, "s", out var v)) View.SetCenter(v); });
        _tip.SetToolTip(_txtTdiv, "예: 10u, 500n, 2ms — Enter로 적용");
        _tip.SetToolTip(_txtCenter, "예: 0, -25u, 1.5m — Enter로 적용");

        // --- all channels
        var aBox = Group("전체 채널");
        var aFlow = (FlowLayoutPanel)aBox.Controls[0];
        aFlow.Controls.Add(Row(Wide("스코프 설정", (_, _) => View.ResetAllToScope(), 85),
                               Wide("모두 자동", (_, _) => View.AutoScaleAll(), 80),
                               Wide("분리 배치", (_, _) => View.StackChannels(), 80)));
        _tip.SetToolTip(aBox, "분리 배치: 채널마다 화면 띠를 나눠서 겹치지 않게 배치");
        _side.Controls.Add(aBox);

        _side.Controls.Add(_chHost);

        _side.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(270, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 0),
            Text = "마우스 조작\n" +
                   "· 휠: 시간 확대/축소 (커서 위치 기준)\n" +
                   "· 드래그: 좌우 이동\n" +
                   "· 우클릭 드래그: 선택 구간 확대\n" +
                   "· 더블클릭 / Home: 전체 보기\n" +
                   "· 왼쪽 채널 마커 드래그: 위치 이동\n" +
                   "· Shift+휠: 선택 채널 V/div\n" +
                   "· Ctrl+휠, ↑↓: 선택 채널 위치\n" +
                   "· ←→: 1 div 이동, +/−: Time/div",
        });

        _scroll.ValueChanged += (_, _) =>
        {
            if (_sync || !View.HasData) return;
            double span = View.T1 - View.T0;
            double t0 = View.FullT0 + _scroll.Value / 10000.0 * Math.Max(0, View.FullSpan - span);
            View.SetCenter(t0 + span / 2);
        };
        View.ViewChanged += (_, _) => SyncFromView();
        View.SelectionChanged += (_, _) => SyncSelection();
        SyncFromView();
    }

    /// <param name="applySavedLabels">false for files loaded from disk: keep the labels stored in the file.</param>
    public void SetWaveforms(List<Waveform> waves, bool applySavedLabels = true)
    {
        if (applySavedLabels)
            foreach (var w in waves)
                if (SavedLabels.TryGetValue(w.Source, out var lbl)) w.Label = lbl;
        View.SetWaveforms(waves);
        RebuildRows();
        SyncFromView();
        SyncSelection();
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
            var txtLabel = new TextBox { Width = 170, PlaceholderText = "라벨 (예: VIN, PWM)", Text = c.Label };
            var txtV = new TextBox { Width = 100 };
            var numPos = new NumericUpDown { Width = 100, DecimalPlaces = 2, Increment = 0.1m, Minimum = -100, Maximum = 100 };
            var stats = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 2, 0, 0) };
            var row = new ChannelRow { C = c, Box = box, Vis = vis, TxtLabel = txtLabel, TxtV = txtV, NumPos = numPos, Stats = stats };

            flow.Controls.Add(Row(vis, txtLabel));
            flow.Controls.Add(Row(Lbl("V/div"),
                Small("▼", (_, _) => { View.Selected = c; View.StepVPerDiv(c, -1); }, "작게 (확대)"),
                txtV,
                Small("▲", (_, _) => { View.Selected = c; View.StepVPerDiv(c, 1); }, "크게 (축소)")));
            flow.Controls.Add(Row(Lbl("위치 (div)"), numPos));
            flow.Controls.Add(Row(
                Wide("자동", (_, _) => { View.Selected = c; WaveformView.AutoScale(c); View.Changed(); }, 60),
                Wide("스코프 설정", (_, _) => { View.Selected = c; if (!WaveformView.ResetToScope(c)) WaveformView.AutoScale(c); View.Changed(); }, 90)));
            flow.Controls.Add(stats);

            stats.Text = $"Min {Eng.Format(c.W.Min, c.Unit)} · Max {Eng.Format(c.W.Max, c.Unit)}\n" +
                         $"Vpp {Eng.Format(c.W.Max - c.W.Min, c.Unit)} · Mean {Eng.Format(c.W.Mean, c.Unit)}" +
                         (Math.Abs(c.OffsetV) > 0 ? $"\n스코프 오프셋 {Eng.Format(c.OffsetV, c.Unit)}" : "");

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
            }, keepText: true);
            numPos.ValueChanged += (_, _) => { if (_sync) return; c.PosDiv = (double)numPos.Value; View.Changed(); };
            _tip.SetToolTip(txtV, "예: 500m, 2, 100mV — Enter로 적용");
            _tip.SetToolTip(txtLabel, "그래프·값 표시·CSV 헤더에 쓰입니다. 채널별로 기억됩니다.");

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
                _lblInfo.Text = "파형 없음";
                _scroll.Enabled = false;
                return;
            }
            string xu = View.Channels[0].W.XUnit;
            if (!_txtTdiv.Modified) _txtTdiv.Text = Eng.Format(View.TPerDiv, xu);
            if (!_txtCenter.Modified) _txtCenter.Text = Eng.Format(View.TCenter, xu);
            int total = View.Channels.Max(c => c.W.Length);
            _lblInfo.Text =
                $"샘플 간격 {Eng.Format(View.MinDx, xu)} ({Eng.Format(1 / View.MinDx, "S/s")})\n" +
                $"표시 {Eng.Format(View.T0, xu)} ~ {Eng.Format(View.T1, xu)}\n" +
                $"화면 {View.PointsInView():N0} / 전체 {total:N0} pts\n" +
                $"전체 길이 {Eng.Format(View.FullSpan, xu)}";

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
            r.Box.Text = sel ? "▶ 선택됨" : "";
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

    private Button Small(string t, EventHandler h, string tip)
    {
        var b = new Button { Text = t, Width = 28, Height = 25, Margin = new Padding(1), FlatStyle = FlatStyle.System };
        b.Click += h;
        _tip.SetToolTip(b, tip);
        return b;
    }

    private static Button Wide(string t, EventHandler h, int w)
    {
        var b = new Button { Text = t, Width = w, Height = 27, Margin = new Padding(1) };
        b.Click += h;
        return b;
    }
}
