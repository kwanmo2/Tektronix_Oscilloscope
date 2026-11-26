using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TekCapture;

namespace TekMcp;

[McpServerToolType]
public static partial class TickTools
{
    [GeneratedRegex(@"^(?<ch>.+?)\s+(?<dir>rise|rising|fall|falling|up|down|↑|↓|상승|하강)(\s*#?\s*(?<n>\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex EdgeRx();

    [McpServerTool(Name = "measure_ticks")]
    [Description("""
        틱(시간 커서) A와 B 사이를 측정한다: A·B 시각, Δt(B−A), 1/Δt, 채널별 A·B 시점 값과 ΔV, A–B 구간의 최소/최대/Vpp/평균/RMS,
        구간 안의 엣지(라벨 이름과 A 기준 시간), A·B에 가장 가까운 엣지. a/b를 비우면 TekCapture 뷰어에서 사용자가 설정한 틱을 쓴다
        (뷰어의 파형 CSV를 자동으로 연다). a/b에는 시간("12.5u", "-3e-6", "0")이나 엣지("VIN rise", "PWM_H fall 3" = 3번째 하강 엣지)를 줄 수 있다.
        B의 엣지 번호는 A 이후부터 센다. 지연·상승 구간·펄스 폭·두 신호 사이 시간차를 잴 때 사용.
        """)]
    public static CallToolResult MeasureTicks(
        [Description("틱 A: 시간 [s] 또는 '<라벨|CHn> rise|fall [n]'. 비우면 뷰어의 틱 A")] string? a = null,
        [Description("틱 B: 시간 [s] 또는 '<라벨|CHn> rise|fall [n]' (A 이후 n번째). 비우면 뷰어의 틱 B")] string? b = null,
        [Description("A–B 주변 그래프 이미지 포함")] bool include_plot = true,
        [Description("측정한 A/B를 TekCapture 뷰어에도 표시 (같은 CSV를 보고 있을 때)")] bool show_in_app = true)
        => ScopeTools.Guard(() => Run(a, b, include_plot, show_in_app));

    private static CallToolResult Run(string? a, string? b, bool include_plot, bool show_in_app)
    {
        {
            var notes = new List<string>();
            var gui = TickFile.Load();
            bool fromGui = string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);

            // data: the viewer's CSV when its ticks are used, otherwise the current set
            var set = Session.Current;
            if ((fromGui || set == null) && gui?.Source is { } src && File.Exists(src) &&
                !string.Equals(set?.CsvPath, src, StringComparison.OrdinalIgnoreCase))
            {
                set = new WaveSet { Waves = CsvImport.Read(src), Origin = "CSV 파일 (TekCapture 뷰어에서 보는 파형)", Time = File.GetLastWriteTime(src), CsvPath = src };
                Session.Current = set;
                notes.Add($"뷰어가 보고 있는 파형 `{src}`을 불러왔습니다.");
            }
            if (set == null) throw new InvalidOperationException("파형이 없습니다. capture_waveforms나 load_waveform_csv를 먼저 호출하세요.");
            if (fromGui && gui != null && gui.Source == null)
                notes.Add("뷰어 파형이 CSV로 저장되지 않아, 뷰어의 틱 시간을 현재 MCP 파형에 적용했습니다. 같은 캡처인지 확인하세요.");

            double ta = Resolve(set, a, gui?.A, null, "A");
            double tb = Resolve(set, b, gui?.B, ta, "B");
            if (fromGui && gui != null) notes.Add($"TekCapture 뷰어에서 설정한 틱을 사용했습니다 ({gui.Updated:HH:mm:ss}).");

            if (show_in_app && !fromGui)
                new TickFile(ta, tb, set.CsvPath, DateTime.Now).Save();

            string text = Build(set, ta, tb, notes);
            byte[]? png = null;
            if (include_plot)
            {
                double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb), pad = Math.Max((hi - lo) * 0.25, set.Waves[0].XIncr * 20);
                png = Plot.Render(set.Waves, lo - pad, hi + pad, tickA: ta, tickB: tb);
            }
            var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
            if (png != null) content.Add(ImageContentBlock.FromBytes(png, "image/png"));
            return new CallToolResult { Content = content };
        }
    }

    /// <summary>Time, edge expression, or the viewer's tick.</summary>
    private static double Resolve(WaveSet set, string? spec, double? guiTick, double? after, string name)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return guiTick ?? throw new InvalidOperationException(
                $"틱 {name}이(가) 없습니다. TekCapture 파형 뷰어에서 {name} 키나 Ctrl+클릭으로 틱을 놓거나, 인자로 시간/엣지를 주세요.");

        var s = spec.Trim();
        if (Eng.TryParse(s, "s", out var t)) return t;

        var m = EdgeRx().Match(s);
        if (!m.Success) throw new ArgumentException($"틱 {name} '{spec}'을(를) 해석할 수 없습니다. 예: \"12.5u\", \"VIN rise\", \"CH2 fall 3\"");
        var w = Names.Find(set.Waves, m.Groups["ch"].Value)
                ?? throw new ArgumentException($"채널 '{m.Groups["ch"].Value}'이(가) 없습니다. 있는 채널: {string.Join(", ", set.Waves.Select(Names.Of))}");
        bool rise = m.Groups["dir"].Value.ToLowerInvariant() is "rise" or "rising" or "up" or "↑" or "상승";
        int n = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
        var meas = Analyzer.Measure(w);
        var edges = (rise ? meas.Rise : meas.Fall).Where(e => after == null || e > after.Value).ToList();
        if (n < 1 || n > edges.Count)
            throw new ArgumentException($"{Names.Of(w)}의 {(rise ? "상승" : "하강")} 엣지가 {(after != null ? "A 이후 " : "")}{edges.Count}개뿐입니다 (요청 {n}번째).");
        return edges[n - 1];
    }

    private static string Build(WaveSet set, double ta, double tb, List<string> notes)
    {
        var w0 = set.Waves[0];
        string xu = w0.XUnit;
        string F(double v, string u) => Eng.Format(v, u);
        double dt = tb - ta;

        var sb = new StringBuilder();
        sb.AppendLine("# 틱 A–B 측정");
        sb.AppendLine($"- 데이터: {set.Origin}" + (set.CsvPath != null ? $" — `{set.CsvPath}`" : ""));
        foreach (var n in notes) sb.AppendLine($"- 참고: {n}");
        sb.AppendLine($"- **A = {F(ta, xu)}**, **B = {F(tb, xu)}** (트리거 t = 0 기준)");
        sb.AppendLine($"- **Δt (B − A) = {F(dt, xu)}**" + (dt != 0 ? $", 1/Δt = {F(1 / Math.Abs(dt), "Hz")}" : "") +
                      $", 샘플 {Math.Abs(dt) / w0.XIncr:N0}개 (샘플 간격 {F(w0.XIncr, xu)} → 시간 분해능 ±{F(w0.XIncr / 2, xu)})");
        sb.AppendLine();

        sb.AppendLine("## 채널별 값");
        sb.AppendLine("| 신호 | A 시점 | B 시점 | Δ (B−A) | 구간 최소 | 구간 최대 | 구간 Vpp | 구간 평균 | 구간 RMS | 기울기 ΔV/Δt |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var w in set.Waves)
        {
            var m = TickMeasure.Measure(w, ta, tb);
            string u = w.YUnit;
            string O(double? v) => v is double x ? F(x, u) : "-";
            string slope = m.Delta is double d && dt != 0 ? F(d / dt, u + "/s") : "-";
            sb.AppendLine($"| {Names.Of(w)} | {O(m.AtA)} | {O(m.AtB)} | {O(m.Delta)} | {F(m.Min, u)} | {F(m.Max, u)} | {F(m.Vpp, u)} | {F(m.Mean, u)} | {F(m.Rms, u)} | {slope} |");
        }
        sb.AppendLine();

        // edges near / between the ticks
        double lo = Math.Min(ta, tb), hi = Math.Max(ta, tb);
        var near = new List<string>();
        var inside = new List<(double T, string Text)>();
        foreach (var w in set.Waves)
        {
            var m = Analyzer.Measure(w);
            if (m.Kind is SignalKind.Flat or SignalKind.Noise or SignalKind.Dc) continue;
            var all = m.Rise.Select(t => (t, up: true)).Concat(m.Fall.Select(t => (t, up: false))).OrderBy(e => e.t).ToList();
            if (all.Count == 0) continue;
            string Near(double tick)
            {
                var e = all.MinBy(x => Math.Abs(x.t - tick));
                double d = e.t - tick;
                return $"{(e.up ? "상승" : "하강")} 엣지 {F(Math.Abs(d), xu)} {(d >= 0 ? "뒤" : "앞")}";
            }
            near.Add($"- {Names.Short(w)}: A에서 {Near(ta)}, B에서 {Near(tb)}");
            foreach (var e in all.Where(e => e.t >= lo && e.t <= hi))
                inside.Add((e.t, $"{Names.Short(w)} {(e.up ? "상승 ↑" : "하강 ↓")}"));
        }
        if (near.Count > 0)
        {
            sb.AppendLine("## 틱과 가장 가까운 엣지 (50 % 레벨 통과 기준)");
            sb.AppendLine("틱이 어떤 엣지를 짚고 있는지 판단할 때 쓰세요. 수 ns 이내면 그 엣지에 놓인 것입니다.");
            foreach (var s in near) sb.AppendLine(s);
            sb.AppendLine();
        }
        sb.AppendLine("## A–B 사이 엣지 (시간순)");
        if (inside.Count == 0) sb.AppendLine("- 구간 안에 엣지가 없습니다.");
        else
        {
            foreach (var (t, txt) in inside.OrderBy(e => e.T).Take(40))
                sb.AppendLine($"- A + {F(t - ta, xu)}  (t = {F(t, xu)}): {txt}");
            if (inside.Count > 40) sb.AppendLine($"- … 외 {inside.Count - 40}개");
        }
        sb.AppendLine();
        sb.AppendLine($"_구간 전체 자동 분석은 analyze_window(t_start={ta.ToString("G9", CultureInfo.InvariantCulture)}, t_end={tb.ToString("G9", CultureInfo.InvariantCulture)})로 볼 수 있습니다. 설명할 때는 신호를 라벨 이름으로 부르세요._");
        return sb.ToString();
    }
}
