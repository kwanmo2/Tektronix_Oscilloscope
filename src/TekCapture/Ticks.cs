using System.Text.Json;

namespace TekCapture;

/// <summary>Time ticks (cursors) A and B shared between the viewer and TekMcp
/// via %APPDATA%\TekCapture\ticks.json.</summary>
public sealed record TickFile(double? A, double? B, string? Source, DateTime Updated)
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TekCapture", "ticks.json");

    public static TickFile? Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<TickFile>(File.ReadAllText(Path)) : null; }
        catch { return null; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch { /* best effort */ }
    }
}

/// <summary>Per-channel values at the ticks and statistics between them.</summary>
public sealed record TickChannel(Waveform W, double? AtA, double? AtB, double Min, double Max, double Mean, double Rms, int Samples)
{
    public double? Delta => AtA.HasValue && AtB.HasValue ? AtB - AtA : null;
    public double Vpp => Max - Min;
}

public static class TickMeasure
{
    /// <summary>Linear interpolation of the sample value at time t (null outside the record).</summary>
    public static double? ValueAt(Waveform w, double t)
    {
        double x = (t - w.XZero) / w.XIncr + w.PtOff;
        if (x < 0 || x > w.Length - 1) return null;
        int i = Math.Min((int)Math.Floor(x), w.Length - 2);
        double f = x - i;
        return w.Y[i] + (w.Y[i + 1] - w.Y[i]) * f;
    }

    public static TickChannel Measure(Waveform w, double a, double b)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        // samples bracketing the ticks, so the stats include the interpolated end values
        int i0 = Math.Clamp((int)Math.Floor((lo - w.XZero) / w.XIncr) + w.PtOff, 0, w.Length - 1);
        int i1 = Math.Clamp((int)Math.Ceiling((hi - w.XZero) / w.XIncr) + w.PtOff, 0, w.Length - 1);
        double mn = double.MaxValue, mx = double.MinValue, sum = 0, sq = 0;
        for (int i = i0; i <= i1; i++)
        {
            double v = w.Y[i];
            if (v < mn) mn = v;
            if (v > mx) mx = v;
            sum += v; sq += v * v;
        }
        int n = i1 - i0 + 1;
        return new TickChannel(w, ValueAt(w, a), ValueAt(w, b), mn, mx, sum / n, Math.Sqrt(sq / n), n);
    }

    /// <summary>Nearest 50 % crossing (edge) of <paramref name="w"/> within ±<paramref name="halfWindow"/> of t.
    /// The 50 % level is taken from the min/max inside the window, so it follows the local edge.</summary>
    public static double? SnapToEdge(Waveform w, double t, double halfWindow)
    {
        int a = Math.Clamp(w.IndexAt(t - halfWindow), 0, w.Length - 1), b = Math.Clamp(w.IndexAt(t + halfWindow), 0, w.Length - 1);
        if (b - a < 2) return null;
        float mn = float.MaxValue, mx = float.MinValue;
        for (int i = a; i <= b; i++) { var v = w.Y[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
        if (mx - mn <= 0) return null;
        double mid = (mn + mx) / 2.0;
        double? best = null;
        for (int i = a; i < b; i++)
        {
            double y0 = w.Y[i], y1 = w.Y[i + 1];
            if ((y0 < mid) == (y1 < mid)) continue;
            double tc = w.Time(i) + (mid - y0) / (y1 - y0) * w.XIncr;
            if (best == null || Math.Abs(tc - t) < Math.Abs(best.Value - t)) best = tc;
        }
        return best;
    }

    /// <summary>Multi-line text readout used by the viewer side panel.</summary>
    public static string Readout(IReadOnlyList<Waveform> waves, double? a, double? b)
    {
        if (waves.Count == 0) return Loc.T("파형 없음", "No waveforms");
        string xu = waves[0].XUnit;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"A  {(a is double ta ? Eng.Format(ta, xu) : "-")}");
        sb.AppendLine($"B  {(b is double tb ? Eng.Format(tb, xu) : "-")}");
        if (a is not double A || b is not double B) return sb.Append(Loc.T("A와 B를 모두 설정하면 시간 차이가 나옵니다.", "Set both A and B to see the time difference.")).ToString();
        double dt = B - A;
        sb.AppendLine($"Δt (B−A)  {Eng.Format(dt, xu)}");
        if (Math.Abs(dt) > 0) sb.AppendLine($"1/Δt  {Eng.Format(1 / Math.Abs(dt), "Hz")}");
        foreach (var w in waves)
        {
            var m = Measure(w, A, B);
            string u = w.YUnit;
            sb.AppendLine();
            sb.AppendLine(w.DisplayName);
            sb.AppendLine($"  A {Fmt(m.AtA, u)} · B {Fmt(m.AtB, u)} · Δ {Fmt(m.Delta, u)}");
            sb.AppendLine(Loc.T("  구간 ", "  Span ") + $"Min {Eng.Format(m.Min, u)} · Max {Eng.Format(m.Max, u)}");
            sb.AppendLine(Loc.T("  구간 ", "  Span ") + $"Vpp {Eng.Format(m.Vpp, u)} · " + Loc.T("평균 ", "Mean ") + Eng.Format(m.Mean, u));
            if (m.Delta is double d && Math.Abs(dt) > 0) sb.AppendLine(Loc.T("  기울기 ", "  Slope ") + $"ΔV/Δt {Eng.Format(d / dt, u + "/s")}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Fmt(double? v, string u) => v is double x ? Eng.Format(x, u) : "-";
}
