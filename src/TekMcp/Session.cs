using System.Globalization;
using System.Text;
using TekCapture;

namespace TekMcp;

/// <summary>Scope settings captured alongside the waveforms, for the report header.</summary>
public sealed class ScopeMeta
{
    public string Idn = "";
    public TekScope.HorizontalInfo? Horizontal;
    public string Trigger = "";
    public string? TriggerSource;
    public double? TriggerLevel;
    public string Acquisition = "";
    public Dictionary<string, string> Channel = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A set of waveforms being analysed (last capture or loaded CSV).</summary>
public sealed class WaveSet
{
    public required List<Waveform> Waves { get; init; }
    public required string Origin { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public string? CsvPath { get; set; }
    public ScopeMeta? Meta { get; init; }
    public List<string> Notes { get; } = [];
}

/// <summary>Holds the current waveform set and serialises access to the scope.
/// The VISA session is opened per operation so the TekCapture GUI can stay connected at the same time.</summary>
public static class Session
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    public static WaveSet? Current { get; set; }

    public static WaveSet RequireCurrent() => Current
        ?? throw new InvalidOperationException("분석할 파형이 없습니다. 먼저 capture_waveforms 또는 load_waveform_csv를 호출하세요.");

    public static string FindResource(string? resource)
    {
        if (!string.IsNullOrWhiteSpace(resource)) return resource.Trim();
        var env = Environment.GetEnvironmentVariable("TEK_RESOURCE");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var all = VisaSession.FindResources();
        var tek = all.FirstOrDefault(r => r.Contains("0x0699", StringComparison.OrdinalIgnoreCase)); // Tektronix USB VID
        if (tek != null) return tek;
        var saved = AppSettings.Resource;
        if (!string.IsNullOrWhiteSpace(saved) && all.Contains(saved, StringComparer.OrdinalIgnoreCase)) return saved;
        throw new InvalidOperationException("Tektronix 스코프(USB VID 0x0699)를 찾지 못했습니다. 발견된 VISA 리소스: " +
                                            (all.Count == 0 ? "(없음)" : string.Join(", ", all)));
    }

    public static T WithScope<T>(string? resource, Func<TekScope, T> f)
    {
        s_gate.Wait();
        try
        {
            using var scope = new TekScope(FindResource(resource));
            return f(scope);
        }
        finally { s_gate.Release(); }
    }

    /// <summary>Labels set with set_channel_labels in this session; applied to later live captures too.</summary>
    public static Dictionary<string, string> LabelOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Effective label for live captures:
    /// set_channel_labels (this session) → TekCapture app label → scope CHx:LABel (already in w.Label).</summary>
    public static void ApplyAppLabels(IEnumerable<Waveform> waves)
    {
        var saved = AppSettings.Labels;
        foreach (var w in waves)
        {
            if (LabelOverrides.TryGetValue(w.Source, out var o)) w.Label = o;
            else if (saved.TryGetValue(w.Source, out var l)) w.Label = l; // same rule as the TekCapture GUI
        }
    }

    // ------------------------------------------------------------------ scope metadata

    private static string Try(TekScope s, string q)
    {
        try { return s.Query(q).Trim().Trim('"'); }
        catch (VisaException) { return ""; }
    }

    private static string Num(string s, string unit) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Eng.Format(v, unit) : s;

    public static ScopeMeta ReadMeta(TekScope s, IEnumerable<string> sources)
    {
        var m = new ScopeMeta { Idn = s.Idn };
        try { m.Horizontal = s.GetHorizontal(); } catch (Exception ex) when (ex is VisaException or FormatException) { }

        var type = Try(s, "TRIGger:A:TYPe?");
        if (type.Equals("EDGE", StringComparison.OrdinalIgnoreCase) || type.Equals("EDG", StringComparison.OrdinalIgnoreCase))
        {
            var src = Try(s, "TRIGger:A:EDGE:SOUrce?");
            var slope = Try(s, "TRIGger:A:EDGE:SLOpe?");
            var lvl = src.StartsWith("CH", StringComparison.OrdinalIgnoreCase) ? Try(s, $"TRIGger:A:LEVel:{src}?") : "";
            var coup = Try(s, "TRIGger:A:EDGE:COUPling?");
            m.TriggerSource = src;
            if (double.TryParse(lvl, NumberStyles.Float, CultureInfo.InvariantCulture, out var lv)) m.TriggerLevel = lv;
            m.Trigger = $"Edge, 소스 {src}, 기울기 {slope}" + (lvl.Length > 0 ? $", 레벨 {Num(lvl, "V")}" : "") + (coup.Length > 0 ? $", 커플링 {coup}" : "");
        }
        else if (type.Length > 0) m.Trigger = type;
        var mode = Try(s, "TRIGger:A:MODe?");
        if (mode.Length > 0) m.Trigger += (m.Trigger.Length > 0 ? ", " : "") + $"모드 {mode}";

        var acq = Try(s, "ACQuire:MODe?");
        var stopAfter = Try(s, "ACQuire:STOPAfter?");
        m.Acquisition = string.Join(", ", new[] { acq.Length > 0 ? $"획득 모드 {acq}" : "", stopAfter.Length > 0 ? $"StopAfter {stopAfter}" : "" }.Where(x => x.Length > 0));

        foreach (var src in sources.Where(x => x.StartsWith("CH", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = new List<string>();
            var coup = Try(s, $"{src}:COUPling?");
            if (coup.Length > 0) parts.Add($"커플링 {coup}");
            var bw = Try(s, $"{src}:BANdwidth?");
            if (bw.Length > 0) parts.Add($"대역폭 {Num(bw, "Hz")}");
            var term = Try(s, $"{src}:TERmination?");
            if (term.Length > 0) parts.Add($"입력 {Num(term, "Ω")}");
            var gain = Try(s, $"{src}:PRObe:GAIN?");
            if (double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0)
                parts.Add($"프로브 {(1 / g).ToString("0.##", CultureInfo.InvariantCulture)}X");
            m.Channel[src] = string.Join(", ", parts);
        }
        return m;
    }

    /// <summary>Reads waveforms; when the scope is free-running, stops it first so every channel
    /// comes from the same acquisition, then resumes.</summary>
    public static WaveSet Capture(string? resource, IReadOnlyList<string>? channels, bool syncStop)
    {
        return WithScope(resource, s =>
        {
            var notes = new List<string>();
            var srcs = channels is { Count: > 0 }
                ? channels.Select(ToSource).ToList()
                : s.DisplayedSources();
            if (srcs.Count == 0) throw new InvalidOperationException("스코프에 표시 중인 채널이 없습니다.");

            bool resumed = false;
            bool running = Try(s, "ACQuire:STATE?") == "1";
            bool runStop = Try(s, "ACQuire:STOPAfter?").StartsWith("RUNST", StringComparison.OrdinalIgnoreCase);
            if (syncStop && running && runStop)
            {
                s.Write("ACQuire:STATE STOP");
                s.Query("*OPC?");
                resumed = true;
            }
            else if (running)
                notes.Add("스코프가 동작(RUN) 중에 채널을 하나씩 읽었습니다. 채널마다 다른 획득일 수 있어 채널 간 시간 관계는 트리거 기준으로만 신뢰할 수 있습니다.");

            try
            {
                var meta = ReadMeta(s, srcs);
                var waves = srcs.Select(s.ReadWaveform).ToList();
                ApplyAppLabels(waves);
                var set = new WaveSet { Waves = waves, Origin = $"{s.Model} 실시간 캡처 ({s.Resource})", Meta = meta };
                set.Notes.AddRange(notes);
                if (resumed) set.Notes.Add("채널 간 동기를 위해 잠시 Stop 후 읽고 다시 Run 상태로 되돌렸습니다.");
                return set;
            }
            finally
            {
                if (resumed) s.Write("ACQuire:STATE RUN");
            }
        });
    }

    /// <summary>"CH1", "1" or a label ("VIN") → scope source name.</summary>
    public static string ToSource(string key)
    {
        var k = key.Trim();
        if (int.TryParse(k, out _)) return "CH" + k;
        if (TekScope.AllSources.Contains(k, StringComparer.OrdinalIgnoreCase)) return k.ToUpperInvariant();
        foreach (var dict in new IReadOnlyDictionary<string, string>[] { LabelOverrides, AppSettings.Labels })
            foreach (var (src, label) in dict)
                if (label.Equals(k, StringComparison.OrdinalIgnoreCase)) return src.ToUpperInvariant();
        throw new ArgumentException($"'{key}'은(는) 채널 이름(CH1~CH4, MATH)도 알려진 라벨도 아닙니다.");
    }

    public static string Stamp(string model) => $"{model}_{DateTime.Now:yyyyMMdd_HHmmss}";

    public static string ModelOf(WaveSet set) =>
        set.Meta?.Idn.Split(',').ElementAtOrDefault(1)?.Trim() is { Length: > 0 } m ? m : "Scope";

    public static string ResolveFolder(string? folder)
    {
        var f = string.IsNullOrWhiteSpace(folder) ? AppSettings.Folder : folder.Trim();
        Directory.CreateDirectory(f);
        return f;
    }

    public static string Describe(Waveform w)
    {
        var sb = new StringBuilder();
        sb.Append($"{Names.Of(w)}: {w.Length:N0} pts, dt {Eng.Format(w.XIncr, w.XUnit)}, {Eng.Format(w.TStart, w.XUnit)} ~ {Eng.Format(w.TEnd, w.XUnit)}");
        return sb.ToString();
    }
}
