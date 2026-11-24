using System.Globalization;
using System.Text;

namespace TekCapture;

public sealed class Waveform
{
    public required string Source { get; init; }
    public required float[] Y { get; init; }
    public double XIncr { get; init; }
    public double XZero { get; init; }
    public int PtOff { get; init; }
    public string XUnit { get; init; } = "s";
    public string YUnit { get; init; } = "V";

    /// <summary>Scope's own vertical settings at capture time (null if unavailable).</summary>
    public double? ScopeVPerDiv { get; init; }
    public double? ScopePosDiv { get; init; }
    public double? ScopeOffset { get; init; }

    /// <summary>User label (e.g. "VIN", "PWM"). Empty → none.</summary>
    public string Label { get; set; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Source : $"{Source} {Label}";

    public int Length => Y.Length;
    public double Time(int i) => XZero + (i - PtOff) * XIncr;
    public int IndexAt(double t) => (int)Math.Round((t - XZero) / XIncr) + PtOff;
    public double TStart => Time(0);
    public double TEnd => Time(Length - 1);

    public float Min { get; private set; }
    public float Max { get; private set; }
    public double Mean { get; private set; }

    public void ComputeStats()
    {
        if (Y.Length == 0) return;
        float mn = float.MaxValue, mx = float.MinValue;
        double sum = 0;
        foreach (var v in Y)
        {
            if (v < mn) mn = v;
            if (v > mx) mx = v;
            sum += v;
        }
        Min = mn; Max = mx; Mean = sum / Y.Length;
    }
}

/// <summary>Tektronix DPO/MSO 4000 series (also works for MDO3000/4000, MSO/DPO2000 and similar).</summary>
public sealed class TekScope : IDisposable
{
    public static readonly string[] AllSources = ["CH1", "CH2", "CH3", "CH4", "MATH"];

    private readonly VisaSession _s;
    private readonly object _lock = new();

    public string Resource { get; }
    public string Idn { get; }
    public string Model => Idn.Split(',').ElementAtOrDefault(1)?.Trim() ?? "Scope";

    public TekScope(string resource)
    {
        Resource = resource;
        _s = new VisaSession(resource, 5000);
        try
        {
            Idn = _s.Query("*IDN?");
            _s.Write("HEADer OFF");
            _s.Write("VERBose ON");
        }
        catch
        {
            _s.Dispose();
            throw;
        }
    }

    private T Locked<T>(Func<T> f)
    {
        lock (_lock)
        {
            try { return f(); }
            catch (VisaException) { _s.Clear(); throw; }
        }
    }

    private void Locked(Action a) => Locked(() => { a(); return 0; });

    private double QD(string q) => double.Parse(_s.Query(q).Trim('"'), CultureInfo.InvariantCulture);

    public string Query(string q) => Locked(() => _s.Query(q));
    public void Write(string c) => Locked(() => _s.Write(c));

    /// <summary>Screen image as PNG bytes (same as the scope's front-panel hardcopy).</summary>
    public byte[] CaptureScreen(bool inkSaver)
    {
        return Locked(() =>
        {
            _s.Write("SAVe:IMAGe:FILEFormat PNG");
            _s.Write($"HARDCopy:INKSaver {(inkSaver ? "ON" : "OFF")}");
            int old = _s.TimeoutMs;
            _s.TimeoutMs = 30000;
            try
            {
                _s.Write("HARDCopy STARt");
                var data = _s.ReadRaw();
                if (data.Length < 8 || data[0] != 0x89 || data[1] != (byte)'P' || data[2] != (byte)'N' || data[3] != (byte)'G')
                    throw new InvalidDataException($"PNG 데이터가 아님 ({data.Length} bytes, 시작: {BitConverter.ToString(data, 0, Math.Min(8, data.Length))})");
                return data;
            }
            finally { _s.TimeoutMs = old; }
        });
    }

    public List<string> DisplayedSources()
    {
        return Locked(() =>
        {
            var list = new List<string>();
            foreach (var src in AllSources)
                if (_s.Query($"SELect:{src}?").Trim() == "1")
                    list.Add(src);
            return list;
        });
    }

    public int RecordLength => Locked(() => (int)QD("HORizontal:RECOrdlength?"));

    public Waveform ReadWaveform(string source)
    {
        return Locked(() =>
        {
            int recLen = (int)QD("HORizontal:RECOrdlength?");
            _s.Write($"DATa:SOUrce {source}");
            _s.Write("DATa:ENCdg SRIbinary");   // signed, little-endian
            _s.Write("WFMOutpre:BYT_Nr 2");
            _s.Write("DATa:STARt 1");
            _s.Write($"DATa:STOP {recLen}");

            double xinc = QD("WFMOutpre:XINcr?");
            double xzero = QD("WFMOutpre:XZEro?");
            int ptoff = (int)QD("WFMOutpre:PT_Off?");
            double ymult = QD("WFMOutpre:YMUlt?");
            double yoff = QD("WFMOutpre:YOFf?");
            double yzero = QD("WFMOutpre:YZEro?");
            string xunit = _s.Query("WFMOutpre:XUNit?").Trim('"');
            string yunit = _s.Query("WFMOutpre:YUNit?").Trim('"');

            // scope vertical settings → lets the viewer start with the same picture as the scope
            double? sv = null, sp = null, so = null;
            try
            {
                if (source.StartsWith("CH", StringComparison.OrdinalIgnoreCase))
                {
                    sv = QD($"{source}:SCAle?"); sp = QD($"{source}:POSition?"); so = QD($"{source}:OFFSet?");
                }
                else if (source.Equals("MATH", StringComparison.OrdinalIgnoreCase))
                {
                    sv = QD("MATH:VERTical:SCAle?"); sp = QD("MATH:VERTical:POSition?"); so = 0;
                }
            }
            catch (Exception ex) when (ex is VisaException or FormatException)
            {
                _s.Clear();
                sv = sp = so = null;
            }

            string label = "";
            if (source.StartsWith("CH", StringComparison.OrdinalIgnoreCase))
            {
                try { label = _s.Query($"{source}:LABel?").Trim().Trim('"').Trim(); }
                catch (VisaException) { _s.Clear(); }
            }

            int old = _s.TimeoutMs;
            _s.TimeoutMs = Math.Max(20000, recLen / 100); // ~10 M pts → 100 s
            byte[] raw;
            try
            {
                _s.Write("CURVe?");
                raw = _s.ReadRaw();
            }
            finally { _s.TimeoutMs = old; }

            // IEEE 488.2 definite-length block: #<n><len><data>
            if (raw.Length < 2 || raw[0] != (byte)'#')
                throw new InvalidDataException($"{source}: 잘못된 CURVe 응답");
            int nDigits = raw[1] - '0';
            int byteLen = int.Parse(Encoding.ASCII.GetString(raw, 2, nDigits), CultureInfo.InvariantCulture);
            int offset = 2 + nDigits;
            if (offset + byteLen > raw.Length)
                throw new InvalidDataException($"{source}: 데이터 길이 부족 ({raw.Length - offset}/{byteLen})");

            int count = byteLen / 2;
            var y = new float[count];
            var span = raw.AsSpan(offset, byteLen);
            for (int i = 0; i < count; i++)
            {
                short code = (short)(span[2 * i] | (span[2 * i + 1] << 8));
                y[i] = (float)((code - yoff) * ymult + yzero);
            }

            var w = new Waveform
            {
                Source = source, Y = y, XIncr = xinc, XZero = xzero, PtOff = ptoff,
                XUnit = string.IsNullOrWhiteSpace(xunit) ? "s" : xunit,
                YUnit = string.IsNullOrWhiteSpace(yunit) ? "V" : yunit,
                ScopeVPerDiv = sv, ScopePosDiv = sp, ScopeOffset = so,
                Label = label,
            };
            w.ComputeStats();
            return w;
        });
    }

    public static readonly int[] RecordLengths = [1_000, 10_000, 100_000, 1_000_000, 10_000_000];

    public sealed record HorizontalInfo(int RecordLength, double TimePerDiv, double SampleRate)
    {
        public double Duration => RecordLength / SampleRate;
    }

    public HorizontalInfo GetHorizontal() => Locked(() => new HorizontalInfo(
        (int)QD("HORizontal:RECOrdlength?"), QD("HORizontal:SCAle?"), QD("HORizontal:SAMPLERate?")));

    /// <summary>Changes record length and tries to keep the current time/div
    /// (the scope raises time/div by itself if the max sample rate would be exceeded).</summary>
    public void SetRecordLength(int points, double? keepTimePerDiv = null) => Locked(() =>
    {
        double scale = keepTimePerDiv ?? QD("HORizontal:SCAle?");
        _s.Write($"HORizontal:RECOrdlength {points}");
        _s.Query("*OPC?");
        _s.Write($"HORizontal:SCAle {scale.ToString("E6", CultureInfo.InvariantCulture)}");
        _s.Query("*OPC?");
    });

    public void Run() =>Locked(() => { _s.Write("ACQuire:STOPAfter RUNSTop"); _s.Write("ACQuire:STATE RUN"); });
    public void Stop() => Locked(() => _s.Write("ACQuire:STATE STOP"));
    public void Single() => Locked(() => { _s.Write("ACQuire:STOPAfter SEQuence"); _s.Write("ACQuire:STATE RUN"); });
    public void Autoset() => Locked(() => { _s.Write("AUTOSet EXECute"); });

    public void Dispose() => _s.Dispose();
}

public static class CsvExport
{
    public static void Write(string path, IReadOnlyList<Waveform> waves)
    {
        if (waves.Count == 0) return;
        var inv = CultureInfo.InvariantCulture;
        var w0 = waves[0];
        bool sharedTime = waves.All(w => w.Length == w0.Length && w.XIncr == w0.XIncr && w.XZero == w0.XZero && w.PtOff == w0.PtOff);
        int rows = waves.Max(w => w.Length);

        using var sw = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        static string Col(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        if (sharedTime)
            sw.WriteLine($"Time ({w0.XUnit})," + string.Join(",", waves.Select(w => Col($"{w.DisplayName} ({w.YUnit})"))));
        else
            sw.WriteLine(string.Join(",", waves.Select(w => Col($"{w.DisplayName} Time ({w.XUnit})") + "," + Col($"{w.DisplayName} ({w.YUnit})"))));

        var sb = new StringBuilder(256);
        for (int i = 0; i < rows; i++)
        {
            sb.Clear();
            if (sharedTime)
            {
                sb.Append(w0.Time(i).ToString("G10", inv));
                foreach (var w in waves) sb.Append(',').Append(w.Y[i].ToString("G7", inv));
            }
            else
            {
                for (int k = 0; k < waves.Count; k++)
                {
                    var w = waves[k];
                    if (k > 0) sb.Append(',');
                    if (i < w.Length) sb.Append(w.Time(i).ToString("G10", inv)).Append(',').Append(w.Y[i].ToString("G7", inv));
                    else sb.Append(',');
                }
            }
            sw.WriteLine(sb);
        }
    }
}

public static class Eng
{
    private static readonly string[] Prefix = ["f", "p", "n", "µ", "m", "", "k", "M", "G", "T"];

    public static string Format(double v, string unit)
    {
        if (Math.Abs(v) < 1e-15 || double.IsNaN(v) || double.IsInfinity(v)) return $"0 {unit}";
        int e = (int)Math.Floor(Math.Log10(Math.Abs(v)) / 3) * 3;
        e = Math.Clamp(e, -15, 12);
        double m = v / Math.Pow(10, e);
        return $"{m.ToString("0.###", CultureInfo.InvariantCulture)} {Prefix[e / 3 + 5]}{unit}";
    }

    /// <summary>Parses "500m", "10us", "2.5 V", "1e-6", "200mV/div" …</summary>
    public static bool TryParse(string text, string unit, out double value)
    {
        value = 0;
        var s = text.Trim().Replace(" ", "");
        if (s.EndsWith("/div", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (unit.Length > 0 && s.Length > unit.Length && s.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) s = s[..^unit.Length];
        if (s.Length == 0) return false;
        double mult = s[^1] switch
        {
            'f' => 1e-15, 'p' => 1e-12, 'n' => 1e-9, 'u' or 'µ' or 'μ' => 1e-6,
            'm' => 1e-3, 'k' or 'K' => 1e3, 'M' => 1e6, 'G' => 1e9, _ => 1,
        };
        if (mult != 1) s = s[..^1];
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) return false;
        value = v * mult;
        return true;
    }
}

public static class Nice
{
    /// <summary>Smallest 1-2-5 value ≥ v.</summary>
    public static double Ceil125(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1.0, 2, 5, 10 })
            if (m * e >= v * (1 - 1e-9)) return m * e;
        return 10 * e;
    }

    /// <summary>Next 1-2-5 value above (dir&gt;0) or below (dir&lt;0) v.</summary>
    public static double Step(double v, int dir)
    {
        double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double[] c = [0.2 * e, 0.5 * e, e, 2 * e, 5 * e, 10 * e, 20 * e];
        return dir > 0 ? c.First(x => x > v * (1 + 1e-9)) : c.Last(x => x < v * (1 - 1e-9));
    }
}
