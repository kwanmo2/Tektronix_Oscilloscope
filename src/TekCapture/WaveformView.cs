using System.Drawing.Drawing2D;

namespace TekCapture;

/// <summary>Per-channel display settings (scope model: div = (V - offset)/scale + position).</summary>
public sealed class ChannelState(Waveform w)
{
    public Waveform W { get; } = w;
    public string Source => W.Source;
    public string Unit => W.YUnit;
    public Color Color => WaveformView.ColorOf(W.Source);
    public string Label { get => W.Label; set => W.Label = value?.Trim() ?? ""; }
    public string DisplayName => W.DisplayName;
    public string Marker => Source.StartsWith("CH") ? Source[2..] : Source[..1];

    public bool Visible { get; set; } = true;
    public double VPerDiv { get; set; } = 1;
    public double PosDiv { get; set; }
    public double OffsetV { get; set; }

    public double ToDiv(double v) => (v - OffsetV) / VPerDiv + PosDiv;

    internal (double, double, Rectangle, double, double, double) CacheKey;
    internal PointF[] CachePts = [];
    internal bool CacheRaw;
}

/// <summary>Scope-style multi-channel waveform viewer.
/// Wheel = time zoom, drag = pan, right-drag = box zoom, Shift+wheel = V/div, Ctrl+wheel = position,
/// drag channel marker = position, double-click = fit.</summary>
public sealed class WaveformView : Control
{
    private static readonly Dictionary<string, Color> Colors = new()
    {
        ["CH1"] = Color.FromArgb(255, 230, 0),
        ["CH2"] = Color.FromArgb(0, 200, 255),
        ["CH3"] = Color.FromArgb(255, 60, 200),
        ["CH4"] = Color.FromArgb(60, 220, 60),
        ["MATH"] = Color.FromArgb(255, 80, 80),
    };
    public static Color ColorOf(string src) => Colors.TryGetValue(src, out var c) ? c : Color.White;

    public const int XDiv = 10, YDiv = 8;
    private const int LeftM = 36, RightM = 12, TopM = 24, BottomM = 46;
    public const double MinVPerDiv = 1e-6, MaxVPerDiv = 1e4;

    public List<ChannelState> Channels { get; } = [];
    public double TCenter { get; private set; }
    public double TPerDiv { get; private set; } = 1e-3;
    public double FullT0 { get; private set; }
    public double FullT1 { get; private set; } = 1e-2;
    public double MinDx { get; private set; } = 1e-9;
    public double T0 => TCenter - TPerDiv * XDiv / 2;
    public double T1 => TCenter + TPerDiv * XDiv / 2;
    public double FullSpan => FullT1 - FullT0;
    public bool HasData => Channels.Count > 0;
    public double MinTPerDiv => MinDx / 5;          // ≥ 2 samples across the screen
    public double MaxTPerDiv => FullSpan / XDiv * 4;

    public event EventHandler? ViewChanged;
    public event EventHandler? SelectionChanged;

    private ChannelState? _selected;
    public ChannelState? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private enum DragMode { None, Pan, Box, Marker }
    private DragMode _drag;
    private int _dragX0, _boxX1;
    private double _dragCenter0;
    private ChannelState? _dragCh;
    private Point? _hover;
    private readonly List<(RectangleF Rect, ChannelState Ch)> _labelRects = [];

    public WaveformView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.Black;
        ForeColor = Color.Gainsboro;
    }

    // ------------------------------------------------------------------ model

    public void SetWaveforms(IEnumerable<Waveform> waves)
    {
        Channels.Clear();
        foreach (var w in waves.Where(w => w.Length > 1))
        {
            var c = new ChannelState(w);
            if (!ResetToScope(c)) AutoScale(c);
            Channels.Add(c);
        }
        if (HasData)
        {
            FullT0 = Channels.Min(c => c.W.TStart);
            FullT1 = Channels.Max(c => c.W.TEnd + c.W.XIncr);   // record covers N·dt (= 10 div on the scope)
            if (FullT1 <= FullT0) FullT1 = FullT0 + 1e-9;
            MinDx = Channels.Min(c => c.W.XIncr);
        }
        _selected = Channels.FirstOrDefault();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        FitTime();
    }

    public void FitTime()
    {
        TPerDiv = FullSpan / XDiv;
        TCenter = (FullT0 + FullT1) / 2;
        Changed();
    }

    public void SetTimePerDiv(double v, double? anchor = null)
    {
        if (!HasData || !double.IsFinite(v) || v <= 0) return;
        v = Math.Clamp(v, MinTPerDiv, MaxTPerDiv);
        double a = anchor ?? TCenter;
        double frac = (a - T0) / (T1 - T0);
        double span = v * XDiv;
        TPerDiv = v;
        TCenter = a - frac * span + span / 2;
        ClampCenter();
        Changed();
    }

    public void SetCenter(double c)
    {
        if (!double.IsFinite(c)) return;
        TCenter = c;
        ClampCenter();
        Changed();
    }

    public void Pan(double divs) => SetCenter(TCenter + divs * TPerDiv);

    private void ClampCenter() => TCenter = Math.Clamp(TCenter, FullT0, FullT1);

    public void Changed()
    {
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool ResetToScope(ChannelState c)
    {
        if (c.W.ScopeVPerDiv is double s && s > 0)
        {
            c.VPerDiv = s;
            c.PosDiv = c.W.ScopePosDiv ?? 0;
            c.OffsetV = c.W.ScopeOffset ?? 0;
            return true;
        }
        return false;
    }

    /// <summary>Fit the channel into <paramref name="bandDivs"/> divisions centered at <paramref name="centerDiv"/>.</summary>
    public static void AutoScale(ChannelState c, double bandDivs = 6, double centerDiv = 0)
    {
        double range = c.W.Max - c.W.Min, mid = (c.W.Max + c.W.Min) / 2.0;
        if (range < 1e-12) range = Math.Max(Math.Abs(mid) * 0.2, 1e-3);
        c.VPerDiv = Math.Clamp(Nice.Ceil125(range / bandDivs), MinVPerDiv, MaxVPerDiv);
        c.PosDiv = centerDiv - (mid - c.OffsetV) / c.VPerDiv;
    }

    public void AutoScaleAll()
    {
        foreach (var c in Channels) AutoScale(c);
        Changed();
    }

    public void ResetAllToScope()
    {
        foreach (var c in Channels) if (!ResetToScope(c)) AutoScale(c);
        FitTime();
    }

    /// <summary>Give each visible channel its own horizontal band so traces don't overlap.</summary>
    public void StackChannels()
    {
        var vis = Channels.Where(c => c.Visible).ToList();
        if (vis.Count == 0) return;
        double band = (double)YDiv / vis.Count;
        for (int k = 0; k < vis.Count; k++)
            AutoScale(vis[k], band * 0.8, YDiv / 2.0 - band * (k + 0.5));
        Changed();
    }

    public void StepVPerDiv(ChannelState c, int dir)
    {
        c.VPerDiv = Math.Clamp(Nice.Step(c.VPerDiv, dir), MinVPerDiv, MaxVPerDiv);
        Changed();
    }

    public int PointsInView()
    {
        var c = Channels.FirstOrDefault(c => c.Visible) ?? Channels.FirstOrDefault();
        if (c == null) return 0;
        int a = Math.Max(0, c.W.IndexAt(T0)), b = Math.Min(c.W.Length - 1, c.W.IndexAt(T1));
        return Math.Max(0, b - a + 1);
    }

    // ------------------------------------------------------------------ geometry

    private Rectangle PlotRect => new(LeftM, TopM, Math.Max(20, Width - LeftM - RightM), Math.Max(20, Height - TopM - BottomM));
    private static float YOfDiv(Rectangle r, double div) => (float)(r.Top + r.Height / 2.0 - div * r.Height / YDiv);
    private double TimeAtX(Rectangle r, float x) => T0 + (x - r.Left) / (double)r.Width * (T1 - T0);

    private ChannelState? MarkerHit(Point p)
    {
        if (p.X > LeftM) return null;
        var r = PlotRect;
        var order = Channels.Where(c => c.Visible).OrderBy(c => c == _selected ? 0 : 1);
        foreach (var c in order)
        {
            float y = Math.Clamp(YOfDiv(r, c.PosDiv), r.Top, r.Bottom);
            if (Math.Abs(p.Y - y) <= 9) return c;
        }
        return null;
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var r = PlotRect;
        using var font = new Font("Segoe UI", 8.5f);
        using var bold = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var textBrush = new SolidBrush(ForeColor);

        DrawGrid(g, r);

        if (!HasData)
        {
            const string msg = "파형 없음 — [파형 캡처]를 누르세요";
            var sz = g.MeasureString(msg, font);
            g.DrawString(msg, font, textBrush, r.Left + (r.Width - sz.Width) / 2, r.Top + (r.Height - sz.Height) / 2);
            return;
        }

        string xUnit = Channels[0].W.XUnit;

        // top info line
        g.DrawString($"{Eng.Format(TPerDiv, xUnit)}/div    중심 {Eng.Format(TCenter, xUnit)}    샘플 간격 {Eng.Format(MinDx, xUnit)}    화면 {PointsInView():N0} pts",
            font, textBrush, r.Left, 4);
        const string help = "휠: 시간 확대 · 드래그: 이동 · 우클릭 드래그: 구간 확대 · Shift+휠: V/div · 더블클릭: 전체";
        var hsz = g.MeasureString(help, font);
        using (var dim = new SolidBrush(Color.FromArgb(120, 120, 120)))
            if (r.Right - hsz.Width > r.Left + 560) g.DrawString(help, font, dim, r.Right - hsz.Width, 4);

        // time labels
        var sfC = new StringFormat { Alignment = StringAlignment.Center };
        for (int i = 0; i <= XDiv; i += 2)
        {
            double t = T0 + TPerDiv * i;
            g.DrawString(Eng.Format(t, xUnit), font, textBrush, new RectangleF(r.Left + r.Width * i / (float)XDiv - 55, r.Bottom + 3, 110, 16), sfC);
        }

        // channel scale labels (click to select)
        _labelRects.Clear();
        float lx = r.Left;
        foreach (var c in Channels)
        {
            string t = $"{c.DisplayName}  {Eng.Format(c.VPerDiv, c.Unit)}/div";
            var f = c == _selected ? bold : font;
            var sz = g.MeasureString(t, f);
            var rect = new RectangleF(lx, r.Bottom + 22, sz.Width + 8, 19);
            using var b = new SolidBrush(c.Visible ? c.Color : Color.FromArgb(90, 90, 90));
            if (c == _selected) using (var p = new Pen(c.Color)) g.DrawRectangle(p, rect.X, rect.Y, rect.Width, rect.Height);
            g.DrawString(t, f, b, rect.X + 4, rect.Y + 2);
            _labelRects.Add((rect, c));
            lx += rect.Width + 10;
        }

        // traces
        g.SetClip(r);
        foreach (var c in Channels.Where(c => c.Visible).OrderBy(c => c == _selected ? 1 : 0))
        {
            var pts = GetPoints(c, r);
            if (pts.Length < 2) continue;
            using var pen = new Pen(c.Color, c == _selected ? 1.6f : 1f);
            g.DrawLines(pen, pts);
            if (c.CacheRaw && pts.Length > 1 && pts[1].X - pts[0].X >= 6)
            {
                using var b = new SolidBrush(c.Color);
                foreach (var p in pts) g.FillEllipse(b, p.X - 2.5f, p.Y - 2.5f, 5, 5);
            }
        }

        if (_drag == DragMode.Box)
        {
            int a = Math.Min(_dragX0, _boxX1), b = Math.Max(_dragX0, _boxX1);
            using var sel = new SolidBrush(Color.FromArgb(50, 255, 255, 255));
            g.FillRectangle(sel, a, r.Top, b - a, r.Height);
        }

        if (_hover is Point hp && r.Contains(hp) && _drag == DragMode.None)
            DrawReadout(g, r, hp, font, textBrush);
        g.ResetClip();

        // channel markers on the left
        foreach (var c in Channels.Where(c => c.Visible).OrderBy(c => c == _selected ? 1 : 0))
        {
            double div = c.PosDiv;
            float y = Math.Clamp(YOfDiv(r, div), r.Top, r.Bottom);
            var poly = new PointF[] { new(2, y - 8), new(LeftM - 12, y - 8), new(LeftM - 3, y), new(LeftM - 12, y + 8), new(2, y + 8) };
            using var b = new SolidBrush(c.Color);
            g.FillPolygon(b, poly);
            if (c == _selected) g.DrawPolygon(Pens.White, poly);
            string m = c.Marker + (div > YDiv / 2.0 ? "↑" : div < -YDiv / 2.0 ? "↓" : "");
            g.DrawString(m, bold, Brushes.Black, 4, y - 8);
        }
    }

    private static void DrawGrid(Graphics g, Rectangle r)
    {
        using var gridPen = new Pen(Color.FromArgb(70, 70, 70)) { DashStyle = DashStyle.Dot };
        for (int i = 0; i <= XDiv; i++) { int x = r.Left + r.Width * i / XDiv; g.DrawLine(gridPen, x, r.Top, x, r.Bottom); }
        for (int i = 0; i <= YDiv; i++) { int y = r.Top + r.Height * i / YDiv; g.DrawLine(gridPen, r.Left, y, r.Right, y); }
        using var axisPen = new Pen(Color.FromArgb(110, 110, 110));
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
        for (int i = 0; i <= XDiv * 5; i++) { float x = r.Left + r.Width * i / (XDiv * 5f); g.DrawLine(axisPen, x, cy - 3, x, cy + 3); }
        for (int i = 0; i <= YDiv * 5; i++) { float y = r.Top + r.Height * i / (YDiv * 5f); g.DrawLine(axisPen, cx - 3, y, cx + 3, y); }
        using var border = new Pen(Color.FromArgb(130, 130, 130));
        g.DrawRectangle(border, r);
    }

    private void DrawReadout(Graphics g, Rectangle r, Point hp, Font font, Brush textBrush)
    {
        using var linePen = new Pen(Color.FromArgb(160, 255, 255, 255)) { DashStyle = DashStyle.Dash };
        g.DrawLine(linePen, hp.X, r.Top, hp.X, r.Bottom);
        double t = TimeAtX(r, hp.X);
        var lines = new List<(string Text, Brush Brush)> { ($"t = {Eng.Format(t, Channels[0].W.XUnit)}", textBrush) };
        var brushes = new List<SolidBrush>();
        foreach (var c in Channels.Where(c => c.Visible))
        {
            int i = c.W.IndexAt(t);
            if (i < 0 || i >= c.W.Length) continue;
            var b = new SolidBrush(c.Color);
            brushes.Add(b);
            lines.Add(($"{c.DisplayName}: {Eng.Format(c.W.Y[i], c.Unit)}", b));
        }
        float w = lines.Max(l => g.MeasureString(l.Text, font).Width) + 12, h = lines.Count * 16 + 6;
        float bx = hp.X + 12 + w > r.Right ? hp.X - 12 - w : hp.X + 12;
        float by = Math.Clamp(hp.Y + 12, r.Top + 2, r.Bottom - h - 2);
        using (var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillRectangle(bg, bx, by, w, h);
        g.DrawRectangle(Pens.DimGray, bx, by, w, h);
        for (int k = 0; k < lines.Count; k++) g.DrawString(lines[k].Text, font, lines[k].Brush, bx + 6, by + 3 + k * 16);
        foreach (var b in brushes) b.Dispose();
    }

    private PointF[] GetPoints(ChannelState c, Rectangle r)
    {
        var key = (T0, T1, r, c.VPerDiv, c.PosDiv, c.OffsetV);
        if (c.CacheKey == key && c.CachePts.Length > 0) return c.CachePts;
        c.CachePts = BuildPoints(c, r, out c.CacheRaw);
        c.CacheKey = key;
        return c.CachePts;
    }

    private PointF[] BuildPoints(ChannelState c, Rectangle r, out bool raw)
    {
        raw = false;
        var w = c.W;
        double t0 = T0, tSpan = T1 - T0;
        int i0 = Math.Max(0, w.IndexAt(t0) - 1);
        int i1 = Math.Min(w.Length - 1, w.IndexAt(T1) + 1);
        if (i1 <= i0) return [];
        int n = i1 - i0 + 1;
        double cy = r.Top + r.Height / 2.0, pxPerDiv = r.Height / (double)YDiv;
        float Y(float v) => (float)Math.Clamp(cy - c.ToDiv(v) * pxPerDiv, r.Top - 5000, r.Bottom + 5000);
        float X(double t) => (float)(r.Left + (t - t0) / tSpan * r.Width);

        if (n <= r.Width * 2)
        {
            raw = true;
            var pts = new PointF[n];
            for (int k = 0; k < n; k++) pts[k] = new PointF(X(w.Time(i0 + k)), Y(w.Y[i0 + k]));
            return pts;
        }

        // min/max per pixel column
        var list = new List<PointF>(r.Width * 2);
        for (int px = 0; px < r.Width; px++)
        {
            int a = Math.Max(i0, w.IndexAt(t0 + tSpan * px / r.Width));
            int b = Math.Min(i1, w.IndexAt(t0 + tSpan * (px + 1) / r.Width) - 1);
            if (b < a) b = a;
            if (a > i1) break;
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = a; i <= b; i++) { var v = w.Y[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
            float x = r.Left + px;
            list.Add(new PointF(x, Y(mn)));
            list.Add(new PointF(x, Y(mx)));
        }
        return [.. list];
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (!HasData) return;
        var r = PlotRect;

        foreach (var (rect, ch) in _labelRects)
            if (rect.Contains(e.Location)) { Selected = ch; return; }

        if (e.Button == MouseButtons.Left)
        {
            if (MarkerHit(e.Location) is ChannelState mc)
            {
                _drag = DragMode.Marker; _dragCh = mc; Selected = mc;
            }
            else if (r.Contains(e.Location))
            {
                _drag = DragMode.Pan; _dragX0 = e.X; _dragCenter0 = TCenter;
                Cursor = Cursors.SizeWE;
            }
        }
        else if (e.Button == MouseButtons.Right && r.Contains(e.Location))
        {
            _drag = DragMode.Box; _dragX0 = _boxX1 = e.X;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var r = PlotRect;
        switch (_drag)
        {
            case DragMode.Marker when _dragCh != null:
                double div = (r.Top + r.Height / 2.0 - e.Y) / (r.Height / (double)YDiv);
                _dragCh.PosDiv = Math.Round(div / 0.02) * 0.02;
                Changed();
                break;
            case DragMode.Pan:
                TCenter = _dragCenter0 - (e.X - _dragX0) / (double)r.Width * (T1 - T0);
                ClampCenter();
                Changed();
                break;
            case DragMode.Box:
                _boxX1 = Math.Clamp(e.X, r.Left, r.Right);
                Invalidate();
                break;
            default:
                _hover = e.Location;
                Cursor = MarkerHit(e.Location) != null ? Cursors.SizeNS : r.Contains(e.Location) ? Cursors.Cross : Cursors.Default;
                Invalidate();
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == DragMode.Box && Math.Abs(_boxX1 - _dragX0) > 4)
        {
            var r = PlotRect;
            double a = TimeAtX(r, Math.Min(_dragX0, _boxX1)), b = TimeAtX(r, Math.Max(_dragX0, _boxX1));
            TPerDiv = Math.Clamp((b - a) / XDiv, MinTPerDiv, MaxTPerDiv);
            TCenter = (a + b) / 2;
            ClampCenter();
            Changed();
        }
        _drag = DragMode.None;
        _dragCh = null;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasData || e.Delta == 0) return;
        if (ModifierKeys.HasFlag(Keys.Shift) && _selected != null)
            StepVPerDiv(_selected, e.Delta > 0 ? -1 : 1);
        else if (ModifierKeys.HasFlag(Keys.Control) && _selected != null)
        {
            _selected.PosDiv += Math.Sign(e.Delta) * 0.2;
            Changed();
        }
        else
            SetTimePerDiv(TPerDiv * Math.Pow(1.25, -e.Delta / 120.0), TimeAtX(PlotRect, e.X));
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (MarkerHit(PointToClient(MousePosition)) == null) FitTime();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!HasData) return;
        switch (e.KeyCode)
        {
            case Keys.Left: Pan(-1); break;
            case Keys.Right: Pan(1); break;
            case Keys.Up when _selected != null: _selected.PosDiv += 0.2; Changed(); break;
            case Keys.Down when _selected != null: _selected.PosDiv -= 0.2; Changed(); break;
            case Keys.Add or Keys.Oemplus: SetTimePerDiv(Nice.Step(TPerDiv, -1)); break;
            case Keys.Subtract or Keys.OemMinus: SetTimePerDiv(Nice.Step(TPerDiv, 1)); break;
            case Keys.Home: FitTime(); break;
        }
    }
}
