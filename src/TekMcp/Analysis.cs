using TekCapture;

namespace TekMcp;

public enum SignalKind { Flat, Noise, Dc, PulseTrain, Transition, AnalogPeriodic, Analog }

/// <summary>Automatic measurements of one channel over a time window.</summary>
public sealed class ChannelMeas
{
    public required Waveform W { get; init; }
    public string Name => Names.Of(W);
    public string Short => Names.Short(W);
    public string U => W.YUnit;
    public int I0, I1;
    public int N => I1 - I0 + 1;
    public double T0 => W.Time(I0);
    public double T1 => W.Time(I1);

    public double Min, Max, Mean, Rms, AcRms;
    public double Vpp => Max - Min;
    public double Top, Base;
    public double Amp => Top - Base;
    public double BimodalFraction;
    /// <summary>RMS deviation of samples around the High/Low levels (noise on the flat parts).</summary>
    public double LevelNoise;
    /// <summary>Vertical quantisation step estimated from the data (0 = unknown).</summary>
    public double Lsb;
    /// <summary>Share of periods within ±25 % of the median (1 = perfectly regular).</summary>
    public double RegularFraction;
    public SignalKind Kind;
    public double Mid, Hys;

    public readonly List<double> Rise = [], Fall = [];
    public double? Freq, PeriodMean, PeriodStd, PeriodMin, PeriodMax, PeriodMedian;
    public double? Duty, PosWidth, NegWidth;
    public double? RiseTime, FallTime;
    public int RiseTimeN, FallTimeN;
    public double? OvershootPct, UndershootPct;
    public readonly List<string> Anomalies = [];
    public readonly List<string> Notes = [];

    public bool HasPeriod => Freq.HasValue;
}

public static class Analyzer
{
    private const int MaxEdges = 200_000;
    /// <summary>Periods within [NormalLo, NormalHi] × median count as normal; outside → glitch / missing pulse.</summary>
    private const double NormalLo = 0.75, NormalHi = 1.25;

    public static ChannelMeas Measure(Waveform w, double? tStart = null, double? tEnd = null)
    {
        int i0 = Math.Clamp(tStart.HasValue ? w.IndexAt(tStart.Value) : 0, 0, w.Length - 1);
        int i1 = Math.Clamp(tEnd.HasValue ? w.IndexAt(tEnd.Value) : w.Length - 1, 0, w.Length - 1);
        if (i1 < i0) (i0, i1) = (i1, i0);
        var m = new ChannelMeas { W = w, I0 = i0, I1 = i1 };
        var y = w.Y.AsSpan(i0, i1 - i0 + 1);
        if (y.Length < 2) { m.Kind = SignalKind.Flat; m.Notes.Add("구간에 샘플이 2개 미만입니다."); return m; }

        // ---- basic stats
        double mn = double.MaxValue, mx = double.MinValue, sum = 0, sq = 0;
        foreach (float v in y)
        {
            if (v < mn) mn = v;
            if (v > mx) mx = v;
            sum += v; sq += (double)v * v;
        }
        m.Min = mn; m.Max = mx; m.Mean = sum / y.Length;
        m.Rms = Math.Sqrt(sq / y.Length);
        m.AcRms = Math.Sqrt(Math.Max(0, sq / y.Length - m.Mean * m.Mean));
        double vpp = mx - mn;
        double scale = w.ScopeVPerDiv ?? 0;

        if (vpp <= 1e-12 * Math.Max(1, Math.Abs(m.Mean)))
        {
            m.Kind = SignalKind.Flat; m.Top = m.Base = m.Mean;
            CheckScreen(m, scale);
            return m;
        }

        // ---- only a couple of ADC codes of activity → no real signal (or a plain DC level)
        m.Lsb = EstimateLsb(y);
        if (m.Lsb > 0 && m.AcRms < 2 * m.Lsb)
        {
            m.Top = mx; m.Base = mn;
            // a real DC level sits well away from 0 compared with its own spread
            m.Kind = Math.Abs(m.Mean) >= 5 * m.Lsb && vpp < 0.5 * Math.Abs(m.Mean) ? SignalKind.Dc : SignalKind.Noise;
            if (m.Kind == SignalKind.Noise)
                m.Notes.Add($"변동이 ADC 몇 단계 수준입니다 (1단계 ≈ {Eng.Format(m.Lsb, m.U)}, AC RMS ≈ {m.AcRms / m.Lsb:F1}단계). " +
                            "프로브가 연결되지 않았거나 신호가 꺼져 있을 수 있습니다. 작은 신호라면 V/div를 줄여 다시 캡처하세요.");
            CheckScreen(m, scale);
            return m;
        }

        // ---- histogram → top/base levels (scope-style "mode" levels)
        const int B = 256;
        var cnt = new int[B];
        var bsum = new double[B];
        foreach (float v in y)
        {
            int b = Math.Clamp((int)((v - mn) / vpp * B), 0, B - 1);
            cnt[b]++; bsum[b] += v;
        }
        int lo = 0, hi = B - 1;
        for (int b = 0; b < B / 2; b++) if (cnt[b] > cnt[lo]) lo = b;
        for (int b = B / 2; b < B; b++) if (cnt[b] > cnt[hi]) hi = b;
        double baseL = cnt[lo] > 0 ? bsum[lo] / cnt[lo] : mn;
        double topL = cnt[hi] > 0 ? bsum[hi] / cnt[hi] : mx;
        int nearLo = 0, nearHi = 0;
        double tol = 0.12 * vpp, dev2 = 0;
        foreach (float v in y)
        {
            if (Math.Abs(v - baseL) < tol) { nearLo++; dev2 += (v - baseL) * (v - baseL); }
            else if (Math.Abs(v - topL) < tol) { nearHi++; dev2 += (v - topL) * (v - topL); }
        }
        if (nearLo + nearHi > 0) m.LevelNoise = Math.Sqrt(dev2 / (nearLo + nearHi));
        m.BimodalFraction = (nearLo + nearHi) / (double)y.Length;
        bool bimodal = m.BimodalFraction >= 0.7 && nearLo > y.Length * 0.02 && nearHi > y.Length * 0.02 && topL - baseL >= 0.5 * vpp;
        // ignore "bimodal" noise on a flat line: needs a step well above 1 vertical division / 10
        if (bimodal && scale > 0 && topL - baseL < 0.2 * scale) bimodal = false;

        if (bimodal)
        {
            m.Top = topL; m.Base = baseL;
            m.Mid = (topL + baseL) / 2;
            // wide enough that noise riding on a slow edge does not count as extra edges
            m.Hys = Math.Min(Math.Max(0.2 * (topL - baseL), 6 * m.LevelNoise), 0.6 * (topL - baseL));
        }
        else
        {
            m.Top = mx; m.Base = mn;
            m.Mid = m.Mean; m.Hys = 0.5 * vpp; // ±25 % of p-p: robust against noise on slow ramps
        }

        FindEdges(m, y);
        Periods(m);

        // ---- classification
        bool dcLike = !bimodal && (
            (Math.Abs(m.Mean) > 0 && vpp < 0.25 * Math.Abs(m.Mean)) ||
            (scale > 0 && vpp < 0.3 * scale));
        if (bimodal)
            m.Kind = m.Rise.Count >= 2 && m.Fall.Count >= 2 ? SignalKind.PulseTrain : SignalKind.Transition;
        else if (dcLike)
            m.Kind = SignalKind.Dc;
        else if (m.HasPeriod && m.RegularFraction >= 0.8 && m.PeriodStd / m.PeriodMean < 0.15)
            m.Kind = SignalKind.AnalogPeriodic;
        else if (m.Rise.Count + m.Fall.Count is > 0 and <= 4 && IsStep(y, m))
            m.Kind = SignalKind.Transition;
        else
            m.Kind = SignalKind.Analog;

        // DC ripple / analog: a "frequency" is only meaningful when the crossings are regular
        if (m.Kind is SignalKind.Dc or SignalKind.Analog && m.HasPeriod &&
            (m.RegularFraction < 0.8 || m.PeriodStd / m.PeriodMean > 0.15))
        {
            m.Freq = m.PeriodMean = m.PeriodStd = m.PeriodMin = m.PeriodMax = m.PeriodMedian = null;
            m.Duty = m.PosWidth = m.NegWidth = null;
            if (m.Kind == SignalKind.Analog)
                m.Notes.Add("평균값 교차 간격이 불규칙합니다 (주기 신호가 아니거나 노이즈가 큼).");
        }

        if (m.Kind is SignalKind.PulseTrain or SignalKind.Transition)
        {
            m.OvershootPct = (mx - m.Top) / m.Amp * 100;
            m.UndershootPct = (m.Base - mn) / m.Amp * 100;
            TransitionTimes(m);
            Glitches(m);
        }

        // ---- acquisition quality hints
        CheckScreen(m, scale);
        if (m.HasPeriod)
        {
            double spp = m.PeriodMean!.Value / w.XIncr;
            if (spp < 10) m.Notes.Add($"주기당 샘플이 {spp:F1}개뿐입니다. 샘플레이트가 부족해 파형 모양·엣지 측정이 부정확할 수 있습니다 (time/div를 줄이거나 레코드 길이를 늘리세요).");
        }
        if (m.Kind == SignalKind.PulseTrain && m.Rise.Count < 3)
            m.Notes.Add("레코드 안의 주기가 3개 미만이라 주파수 통계가 약합니다.");
        if (m.RiseTime is double rt && rt < 3 * w.XIncr)
            m.Notes.Add($"상승시간이 샘플 간격({Eng.Format(w.XIncr, "s")})의 3배 미만이라 샘플링에 의해 제한된 값입니다. 실제 엣지는 더 빠를 수 있습니다.");
        return m;
    }

    /// <summary>Smallest step between distinct sample values (ADC LSB). 0 when the data is not visibly quantised.</summary>
    private static double EstimateLsb(ReadOnlySpan<float> y)
    {
        var set = new HashSet<float>();
        int stride = Math.Max(1, y.Length / 200_000);
        for (int i = 0; i < y.Length; i += stride)
            if (set.Add(y[i]) && set.Count > 4096) return 0;
        if (set.Count < 2) return 0;
        var v = set.ToArray();
        Array.Sort(v);
        double d = double.MaxValue;
        for (int i = 1; i < v.Length; i++) d = Math.Min(d, v[i] - v[i - 1]);
        // float rounding of (code - yoff) * ymult gives tiny spurious steps → ignore below 1e-6 of the span
        return d > (v[^1] - v[0]) * 1e-6 ? d : 0;
    }

    private static bool IsStep(ReadOnlySpan<float> y, ChannelMeas m)
    {
        // start and end settle on clearly different levels
        int k = Math.Max(1, y.Length / 10);
        double a = 0, b = 0;
        for (int i = 0; i < k; i++) { a += y[i]; b += y[y.Length - 1 - i]; }
        a /= k; b /= k;
        return Math.Abs(b - a) > 0.5 * m.Vpp;
    }

    private static void CheckScreen(ChannelMeas m, double scale)
    {
        var w = m.W;
        if (scale <= 0 || w.ScopePosDiv is not double pos) return;
        double off = w.ScopeOffset ?? 0;
        double top = off + (5 - pos) * scale, bot = off + (-5 - pos) * scale;
        if (m.Max >= top - 0.05 * scale) m.Anomalies.Add($"상단 클리핑 의심: 최대값 {Eng.Format(m.Max, m.U)}이 화면 위 끝({Eng.Format(top, m.U)})에 닿았습니다. 측정값이 잘렸을 수 있습니다.");
        if (m.Min <= bot + 0.05 * scale) m.Anomalies.Add($"하단 클리핑 의심: 최소값 {Eng.Format(m.Min, m.U)}이 화면 아래 끝({Eng.Format(bot, m.U)})에 닿았습니다.");
        double used = m.Vpp / scale;
        if (m.Kind is not (SignalKind.Flat or SignalKind.Noise) && used < 1.5)
            m.Notes.Add($"수직 해상도 낮음: 신호가 {used:F2} div만 씁니다 ({Eng.Format(scale, m.U)}/div, 8비트 ADC 기준 약 {Math.Max(1, used * 25):F0}단계). V/div를 줄이면 레벨·리플 측정이 정확해집니다.");
    }

    private static void FindEdges(ChannelMeas m, ReadOnlySpan<float> y)
    {
        var w = m.W;
        double mid = m.Mid, hiT = mid + m.Hys / 2, loT = mid - m.Hys / 2;
        int state = 0; // -1 low, +1 high
        int lastBelow = -1, lastAbove = -1;
        for (int i = 0; i < y.Length; i++)
        {
            float v = y[i];
            if (v < mid) lastBelow = i; else lastAbove = i;
            if (v >= hiT && state != 1)
            {
                if (state == -1 && lastBelow >= 0 && lastBelow + 1 < y.Length && m.Rise.Count < MaxEdges)
                    m.Rise.Add(Cross(w, y, m.I0, lastBelow, mid));
                state = 1;
            }
            else if (v <= loT && state != -1)
            {
                if (state == 1 && lastAbove >= 0 && lastAbove + 1 < y.Length && m.Fall.Count < MaxEdges)
                    m.Fall.Add(Cross(w, y, m.I0, lastAbove, mid));
                state = -1;
            }
        }
    }

    /// <summary>Interpolated time where the signal crosses <paramref name="level"/> between samples k and k+1.</summary>
    private static double Cross(Waveform w, ReadOnlySpan<float> y, int i0, int k, double level)
    {
        double a = y[k], b = y[k + 1];
        double f = b == a ? 0.5 : Math.Clamp((level - a) / (b - a), 0, 1);
        return w.Time(i0 + k) + f * w.XIncr;
    }

    private static void Periods(ChannelMeas m)
    {
        if (m.Rise.Count < 2) return;
        var p = new double[m.Rise.Count - 1];
        for (int i = 0; i < p.Length; i++) p[i] = m.Rise[i + 1] - m.Rise[i];
        // robust: glitches / missing pulses are reported separately and must not skew frequency or jitter
        double med = Median(p);
        var normal = p.Where(x => x >= NormalLo * med && x <= NormalHi * med).ToArray();
        if (normal.Length == 0) normal = p;
        double mean = normal.Average();
        m.PeriodMedian = med;
        m.RegularFraction = normal.Length / (double)p.Length;
        m.PeriodMean = mean;
        m.PeriodStd = Math.Sqrt(normal.Sum(x => (x - mean) * (x - mean)) / normal.Length);
        m.PeriodMin = p.Min(); m.PeriodMax = p.Max();
        m.Freq = 1 / mean;

        // pulse widths: rise → next fall (positive), fall → next rise (negative)
        var pos = new List<double>();
        var neg = new List<double>();
        int f = 0;
        for (int i = 0; i < m.Rise.Count - 1; i++)
        {
            double r = m.Rise[i], rn = m.Rise[i + 1];
            while (f < m.Fall.Count && m.Fall[f] <= r) f++;
            if (f < m.Fall.Count && m.Fall[f] < rn)
            {
                pos.Add(m.Fall[f] - r);
                neg.Add(rn - m.Fall[f]);
            }
        }
        if (pos.Count > 0)
        {
            // medians so one glitch does not shift the typical pulse
            m.PosWidth = Median(pos);
            m.NegWidth = Median(neg);
            m.Duty = m.PosWidth / (m.PosWidth + m.NegWidth) * 100;
        }
    }

    private static void TransitionTimes(ChannelMeas m)
    {
        var w = m.W;
        var mem = w.Y.AsMemory(m.I0, m.N);
        int len = mem.Length;
        double l10 = m.Base + 0.1 * m.Amp, l90 = m.Base + 0.9 * m.Amp;
        int maxSearch = m.PeriodMedian is double pm ? Math.Max(4, (int)(pm / w.XIncr / 2)) : Math.Min(len, 2_000_000);

        double? One(double tEdge, bool rising)
        {
            var y = mem.Span;
            int k = Math.Clamp(w.IndexAt(tEdge) - m.I0, 0, y.Length - 1);
            // search backwards for the start level, forwards for the end level
            double startL = rising ? l10 : l90, endL = rising ? l90 : l10;
            int j = k, lim = Math.Max(0, k - maxSearch);
            while (j > lim && (rising ? y[j] > startL : y[j] < startL)) j--;
            if (rising ? y[j] > startL : y[j] < startL) return null;
            double t1 = j + 1 < y.Length ? Cross(w, y, m.I0, j, startL) : w.Time(m.I0 + j);
            int e = k, elim = Math.Min(y.Length - 1, k + maxSearch);
            while (e < elim && (rising ? y[e] < endL : y[e] > endL)) e++;
            if (rising ? y[e] < endL : y[e] > endL) return null;
            double t2 = e > 0 ? Cross(w, y, m.I0, e - 1, endL) : w.Time(m.I0 + e);
            return t2 - t1;
        }

        List<double> Collect(List<double> edges, bool rising)
        {
            var r = new List<double>();
            int stride = Math.Max(1, edges.Count / 2000);
            for (int i = 0; i < edges.Count; i += stride)
                if (One(edges[i], rising) is double d && d >= 0) r.Add(d);
            return r;
        }

        var rt = Collect(m.Rise, true);
        var ft = Collect(m.Fall, false);
        if (rt.Count > 0) { m.RiseTime = Median(rt); m.RiseTimeN = rt.Count; }
        if (ft.Count > 0) { m.FallTime = Median(ft); m.FallTimeN = ft.Count; }
    }

    private static void Glitches(ChannelMeas m)
    {
        if (m.PeriodMedian is not double pm || m.Rise.Count < 5) return;
        string U(double t) => Eng.Format(t, m.W.XUnit);

        var gaps = new List<double>();
        for (int i = 0; i + 1 < m.Rise.Count; i++)
            if (m.Rise[i + 1] - m.Rise[i] > NormalHi * pm) gaps.Add(m.Rise[i]);
        if (gaps.Count > 0)
            m.Anomalies.Add($"긴 주기(누락 펄스·버스트 사이 간격) {gaps.Count}회: 정상 주기 {U(pm)}보다 25% 이상 긴 간격이 시작된 위치 " +
                            string.Join(", ", gaps.Take(5).Select(U)) + (gaps.Count > 5 ? " …" : ""));

        // an extra pulse splits one period into two short ones → count it once, at the extra edge
        var extra = new List<double>();
        for (int i = 0; i + 1 < m.Rise.Count; i++)
            if (m.Rise[i + 1] - m.Rise[i] < NormalLo * pm)
            {
                extra.Add(m.Rise[i + 1]);
                if (i + 2 < m.Rise.Count && m.Rise[i + 2] - m.Rise[i + 1] < NormalLo * pm) i++;
            }
        if (extra.Count > 0)
            m.Anomalies.Add($"추가 펄스(글리치) {extra.Count}회: 정상 주기 {U(pm)} 안에 엣지가 더 생김. 위치 " +
                            string.Join(", ", extra.Take(5).Select(U)) + (extra.Count > 5 ? " …" : ""));
        var shortP = extra;

        if (m.PeriodStd is double sd && m.PeriodMean is double mean && sd / mean > 0.02 && gaps.Count == 0 && shortP.Count == 0)
            m.Notes.Add($"주기 변동이 큽니다 (표준편차 {sd / mean * 100:F1}%). 주파수 변조·스펙트럼 확산·불안정한 클럭일 수 있습니다.");
    }

    public static double Median(IEnumerable<double> xs)
    {
        var a = xs.ToArray();
        if (a.Length == 0) return double.NaN;
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    // ================================================================== cross-channel

    public sealed class PairMeas
    {
        public required ChannelMeas A, B;
        public double? Corr;
        public double? FreqRatio;
        public double? Delay, Phase;            // A rising → nearest B rising
        public double? DeadAB, DeadBA;          // A falling → B rising, B falling → A rising
        public double? FirstEdgeDelta;
    }

    public static PairMeas Pair(ChannelMeas a, ChannelMeas b)
    {
        var p = new PairMeas { A = a, B = b };
        p.Corr = Correlation(a, b);
        if (a.Freq is double fa && b.Freq is double fb) p.FreqRatio = fa / fb;

        static bool Periodic(ChannelMeas m) => m.Kind is SignalKind.PulseTrain or SignalKind.AnalogPeriodic;
        if (Periodic(a) && Periodic(b))
        {
            if (p.FreqRatio is double r && Math.Abs(r - 1) < 0.02 && a.Rise.Count > 0 && b.Rise.Count > 0)
            {
                double per = a.PeriodMedian ?? a.PeriodMean!.Value;
                var d = a.Rise.Select(t => Nearest(b.Rise, t) - t).Where(x => Math.Abs(x) <= per / 2).ToList();
                if (d.Count > 0)
                {
                    p.Delay = Median(d);
                    p.Phase = p.Delay / per * 360;
                }
            }
        }

        if (a.Kind == SignalKind.PulseTrain && b.Kind == SignalKind.PulseTrain && p.Corr < -0.3 &&
            p.FreqRatio is double r2 && Math.Abs(r2 - 1) < 0.02)
        {
            double per = a.PeriodMedian!.Value;
            var ab = a.Fall.Select(t => Nearest(b.Rise, t) - t).Where(x => Math.Abs(x) < per / 4).ToList();
            var ba = b.Fall.Select(t => Nearest(a.Rise, t) - t).Where(x => Math.Abs(x) < per / 4).ToList();
            if (ab.Count > 0) p.DeadAB = Median(ab);
            if (ba.Count > 0) p.DeadBA = Median(ba);
        }

        double? fa0 = FirstEdge(a), fb0 = FirstEdge(b);
        if (fa0.HasValue && fb0.HasValue && a.Kind == SignalKind.Transition && b.Kind == SignalKind.Transition)
            p.FirstEdgeDelta = fb0 - fa0;
        return p;
    }

    public static double? FirstEdge(ChannelMeas m)
    {
        double? r = m.Rise.Count > 0 ? m.Rise[0] : null, f = m.Fall.Count > 0 ? m.Fall[0] : null;
        return r.HasValue && f.HasValue ? Math.Min(r.Value, f.Value) : r ?? f;
    }

    private static double Nearest(List<double> sorted, double t)
    {
        int i = sorted.BinarySearch(t);
        if (i >= 0) return sorted[i];
        i = ~i;
        if (i == 0) return sorted[0];
        if (i >= sorted.Count) return sorted[^1];
        return t - sorted[i - 1] <= sorted[i] - t ? sorted[i - 1] : sorted[i];
    }

    private static double? Correlation(ChannelMeas a, ChannelMeas b)
    {
        var wa = a.W; var wb = b.W;
        int n = a.N;
        int stride = Math.Max(1, n / 200_000);
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        int cnt = 0;
        for (int i = a.I0; i <= a.I1; i += stride)
        {
            int j = wb.IndexAt(wa.Time(i));
            if (j < b.I0 || j > b.I1) continue;
            double x = wa.Y[i], y = wb.Y[j];
            sa += x; sb += y; saa += x * x; sbb += y * y; sab += x * y; cnt++;
        }
        if (cnt < 10) return null;
        double va = saa / cnt - (sa / cnt) * (sa / cnt), vb = sbb / cnt - (sb / cnt) * (sb / cnt);
        if (va <= 0 || vb <= 0) return null;
        return (sab / cnt - sa / cnt * (sb / cnt)) / Math.Sqrt(va * vb);
    }
}
