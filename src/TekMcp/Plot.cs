using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using TekCapture;

namespace TekMcp;

/// <summary>Renders waveforms as a PNG: one lane per channel (stacked) or all on one grid (overlay),
/// each trace named by its label. Uses a min/max envelope per pixel column so narrow glitches stay visible.</summary>
public static class Plot
{
    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CH1"] = Color.FromArgb(255, 230, 0),
        ["CH2"] = Color.FromArgb(0, 200, 255),
        ["CH3"] = Color.FromArgb(255, 60, 200),
        ["CH4"] = Color.FromArgb(60, 220, 60),
        ["MATH"] = Color.FromArgb(255, 80, 80),
    };
    private static readonly Color[] Extra = [Color.FromArgb(255, 150, 40), Color.FromArgb(170, 130, 255), Color.FromArgb(120, 255, 220), Color.White];

    private static readonly Color TickA = Color.FromArgb(255, 165, 0), TickB = Color.FromArgb(190, 140, 255);

    /// <summary>Color chosen in TekCapture (settings.json "Colors"), else the scope palette.</summary>
    public static Color ColorOf(string src, int index, IReadOnlyDictionary<string, Color>? custom = null) =>
        custom != null && custom.TryGetValue(src, out var u) ? u : Colors.TryGetValue(src, out var c) ? c : Extra[index % Extra.Length];

    public static byte[] Render(IReadOnlyList<Waveform> waves, double? tStart, double? tEnd, bool overlay = false, int width = 1400,
        double? tickA = null, double? tickB = null)
    {
        var w0 = waves[0];
        double t0 = tStart ?? waves.Min(w => w.TStart);
        double t1 = tEnd ?? waves.Max(w => w.TEnd + w.XIncr);
        if (t1 <= t0) t1 = t0 + w0.XIncr * 10;

        const int left = 90, right = 20, top = 34, bottom = 44, laneH = 170, gap = 8;
        int lanes = overlay ? 1 : waves.Count;
        int plotH = overlay ? 420 : lanes * laneH + (lanes - 1) * gap;
        int height = top + plotH + bottom;
        int pw = width - left - right;

        using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(18, 18, 22));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", 9f);
        using var bold = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var gridPen = new Pen(Color.FromArgb(55, 55, 62)) { DashStyle = DashStyle.Dot };
        using var axisPen = new Pen(Color.FromArgb(90, 90, 100));
        using var dim = new SolidBrush(Color.FromArgb(170, 170, 180));

        var custom = AppSettings.Colors;
        string xu = w0.XUnit;
        g.DrawString($"{Eng.Format(t0, xu)} ~ {Eng.Format(t1, xu)}   ({Eng.Format((t1 - t0) / 10, xu)}/div)", font, dim, left, 10);

        for (int li = 0; li < lanes; li++)
        {
            var rect = new Rectangle(left, top + li * (laneH + gap), pw, overlay ? plotH : laneH);
            var laneWaves = overlay ? waves : [waves[li]];

            // vertical range: union of visible data, 8% headroom
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var w in laneWaves)
            {
                int a = Math.Clamp(w.IndexAt(t0), 0, w.Length - 1), b = Math.Clamp(w.IndexAt(t1), 0, w.Length - 1);
                for (int i = a; i <= b; i++) { if (w.Y[i] < lo) lo = w.Y[i]; if (w.Y[i] > hi) hi = w.Y[i]; }
            }
            if (lo > hi) { lo = -1; hi = 1; }
            double span = hi - lo;
            if (span < 1e-12) { span = Math.Max(Math.Abs(hi) * 0.1, 1e-3); }
            lo -= span * 0.08; hi += span * 0.08;

            g.FillRectangle(new SolidBrush(Color.FromArgb(10, 10, 12)), rect);
            for (int k = 1; k < 10; k++) { float x = rect.Left + rect.Width * k / 10f; g.DrawLine(gridPen, x, rect.Top, x, rect.Bottom); }
            for (int k = 1; k < 4; k++) { float y = rect.Top + rect.Height * k / 4f; g.DrawLine(gridPen, rect.Left, y, rect.Right, y); }
            g.DrawRectangle(axisPen, rect);

            float Y(double v) => (float)(rect.Bottom - (v - lo) / (hi - lo) * rect.Height);
            float X(double t) => (float)(rect.Left + (t - t0) / (t1 - t0) * rect.Width);

            // y labels
            for (int k = 0; k <= 4; k++)
            {
                double v = lo + (hi - lo) * k / 4;
                var s = Eng.Format(v, laneWaves[0].YUnit);
                var sz = g.MeasureString(s, font);
                g.DrawString(s, font, dim, rect.Left - sz.Width - 4, Math.Clamp(Y(v) - sz.Height / 2, rect.Top, rect.Bottom - sz.Height));
            }
            // 0 V line and trigger marker
            if (lo < 0 && hi > 0) using (var zp = new Pen(Color.FromArgb(80, 80, 90))) g.DrawLine(zp, rect.Left, Y(0), rect.Right, Y(0));
            if (t0 < 0 && t1 > 0) using (var tp = new Pen(Color.FromArgb(150, 200, 200, 200)) { DashStyle = DashStyle.Dash }) g.DrawLine(tp, X(0), rect.Top, X(0), rect.Bottom);

            // ticks A/B
            if (tickA is double ka && tickB is double kb)
            {
                float xa = Math.Clamp(X(Math.Min(ka, kb)), rect.Left, rect.Right), xb = Math.Clamp(X(Math.Max(ka, kb)), rect.Left, rect.Right);
                if (xb > xa) using (var sh = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillRectangle(sh, xa, rect.Top, xb - xa, rect.Height);
            }
            foreach (var (tk, name, col) in new[] { (tickA, "A", TickA), (tickB, "B", TickB) })
            {
                if (tk is not double tv || tv < t0 || tv > t1) continue;
                using var tpen = new Pen(col, 1.6f) { DashStyle = name == "B" ? DashStyle.Dash : DashStyle.Solid };
                g.DrawLine(tpen, X(tv), rect.Top, X(tv), rect.Bottom);
                if (li == 0) using (var tb = new SolidBrush(col)) g.DrawString(name, bold, tb, X(tv) + 3, rect.Bottom - 22);
            }

            float labelX = rect.Left + 6;
            for (int wi = 0; wi < laneWaves.Count; wi++)
            {
                var w = laneWaves[wi];
                var col = ColorOf(w.Source, overlay ? wi : li, custom);
                DrawTrace(g, w, rect, t0, t1, col, Y);
                using var br = new SolidBrush(col);
                var name = Names.Of(w);
                g.DrawString(name, bold, br, labelX, rect.Top + 4);
                labelX += g.MeasureString(name, bold).Width + 14;
            }
        }

        // time labels
        for (int k = 0; k <= 10; k += 2)
        {
            double t = t0 + (t1 - t0) * k / 10;
            var s = Eng.Format(t, xu);
            var sz = g.MeasureString(s, font);
            float x = left + pw * k / 10f - sz.Width / 2;
            g.DrawString(s, font, dim, Math.Clamp(x, 0, width - sz.Width), top + plotH + 6);
        }
        if (t0 < 0 && t1 > 0) g.DrawString("▼ 트리거 t=0", font, new SolidBrush(Color.FromArgb(200, 200, 200)), left + (float)(-t0 / (t1 - t0) * pw) - 8, top + plotH + 24);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static void DrawTrace(Graphics g, Waveform w, Rectangle r, double t0, double t1, Color col, Func<double, float> Y)
    {
        int a = Math.Clamp(w.IndexAt(t0), 0, w.Length - 1), b = Math.Clamp(w.IndexAt(t1), 0, w.Length - 1);
        if (b <= a) return;
        int n = b - a + 1;
        using var pen = new Pen(col, 1.4f);
        if (n <= r.Width * 2)
        {
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
                pts[i] = new PointF((float)(r.Left + (w.Time(a + i) - t0) / (t1 - t0) * r.Width), Y(w.Y[a + i]));
            if (pts.Length >= 2) g.DrawLines(pen, pts);
            if (n <= r.Width / 6) using (var br = new SolidBrush(col)) foreach (var p in pts) g.FillEllipse(br, p.X - 2, p.Y - 2, 4, 4);
            return;
        }
        // min/max envelope per pixel column
        g.SmoothingMode = SmoothingMode.None;
        float? prevMin = null, prevMax = null;
        for (int px = 0; px < r.Width; px++)
        {
            double ta = t0 + (t1 - t0) * px / r.Width, tb = t0 + (t1 - t0) * (px + 1) / r.Width;
            int ia = Math.Clamp(w.IndexAt(ta), a, b), ib = Math.Clamp(w.IndexAt(tb), a, b);
            if (ib < ia) continue;
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = ia; i <= ib; i++) { var v = w.Y[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
            float y1 = Y(mx), y2 = Y(mn);
            // connect to the previous column so edges stay continuous
            if (prevMin.HasValue) { y1 = Math.Min(y1, prevMin.Value); y2 = Math.Max(y2, prevMax!.Value); }
            g.DrawLine(pen, r.Left + px, y1, r.Left + px, Math.Max(y2, y1 + 1));
            prevMin = Y(mn); prevMax = Y(mx);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
    }
}
