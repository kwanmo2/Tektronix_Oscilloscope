using System.Globalization;
using System.Text;
using TekCapture;

namespace TekMcp;

/// <summary>Builds the Markdown analysis report. Every signal is referred to by its label.</summary>
public static class Report
{
    private static string F(double v, string unit) => Eng.Format(v, unit);
    private static string P(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " %";

    public static string KindText(SignalKind k) => k switch
    {
        SignalKind.Flat => "일정 (변화 없음)",
        SignalKind.Noise => "유효 신호 없음 (ADC 몇 단계 수준의 노이즈)",
        SignalKind.Dc => "DC 레벨 (리플·노이즈 포함)",
        SignalKind.PulseTrain => "디지털/펄스열 (반복)",
        SignalKind.Transition => "상태 전환 (스텝·단발 엣지)",
        SignalKind.AnalogPeriodic => "아날로그 주기 신호",
        _ => "아날로그 (비주기/복합)",
    };

    public static string Build(WaveSet set, IReadOnlyList<Waveform> waves, double? tStart, double? tEnd, string? title = null)
    {
        var ms = waves.Select(w => Analyzer.Measure(w, tStart, tEnd)).ToList();
        var sb = new StringBuilder();
        var w0 = waves[0];
        string xu = w0.XUnit;

        sb.AppendLine($"# {title ?? "파형 분석 보고서"}");
        sb.AppendLine($"- 데이터: {set.Origin}" + (set.CsvPath != null ? $" — 파일 `{set.CsvPath}`" : ""));
        sb.AppendLine($"- 시각: {set.Time:yyyy-MM-dd HH:mm:ss}");
        if (set.Meta is { } meta)
        {
            if (meta.Idn.Length > 0) sb.AppendLine($"- 장비: {meta.Idn}");
            if (meta.Horizontal is { } h)
                sb.AppendLine($"- 수평: {F(h.TimePerDiv, "s")}/div, 샘플레이트 {F(h.SampleRate, "S/s")}, 레코드 {h.RecordLength:N0} pts");
            if (meta.Trigger.Length > 0) sb.AppendLine($"- 트리거: {meta.Trigger} (t = 0이 트리거 시점)");
            if (meta.Acquisition.Length > 0) sb.AppendLine($"- 획득: {meta.Acquisition}");
        }
        var m0 = ms[0];
        sb.AppendLine($"- 분석 구간: {F(m0.T0, xu)} ~ {F(m0.T1, xu)} (폭 {F(m0.T1 - m0.T0, xu)}, 샘플 간격 {F(w0.XIncr, xu)}, {m0.N:N0} pts/채널)");
        foreach (var n in set.Notes) sb.AppendLine($"- 참고: {n}");
        sb.AppendLine();

        // ---- names
        sb.AppendLine("## 신호 이름 (라벨)");
        sb.AppendLine("| 채널 | 라벨 | 단위 | 스코프 설정 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var w in waves)
        {
            var cfg = new List<string>();
            if (w.ScopeVPerDiv is double v) cfg.Add($"{F(v, w.YUnit)}/div");
            if (w.ScopePosDiv is double p) cfg.Add($"위치 {p.ToString("0.##", CultureInfo.InvariantCulture)} div");
            if (w.ScopeOffset is double o && o != 0) cfg.Add($"오프셋 {F(o, w.YUnit)}");
            if (set.Meta?.Channel.TryGetValue(w.Source, out var extra) == true && extra.Length > 0) cfg.Add(extra);
            sb.AppendLine($"| {w.Source} | {(string.IsNullOrWhiteSpace(w.Label) ? "(없음)" : $"**{w.Label}**")} | {w.YUnit} | {string.Join(", ", cfg)} |");
        }
        var unlabeled = waves.Where(w => string.IsNullOrWhiteSpace(w.Label)).Select(w => w.Source).ToList();
        if (unlabeled.Count > 0)
            sb.AppendLine($"\n> 라벨 없는 채널: {string.Join(", ", unlabeled)}. 신호 이름을 알면 set_channel_labels로 붙여 주세요 (예: {{\"{unlabeled[0]}\": \"신호이름\"}}). 분석 설명은 라벨 이름으로 합니다.");
        sb.AppendLine();

        // ---- per channel
        sb.AppendLine("## 채널별 측정");
        foreach (var m in ms) AppendChannel(sb, m);

        // ---- cross channel
        var rel = new List<string>();
        for (int i = 0; i < ms.Count; i++)
            for (int j = i + 1; j < ms.Count; j++)
            {
                if (ms[i].Kind is SignalKind.Flat or SignalKind.Noise || ms[j].Kind is SignalKind.Flat or SignalKind.Noise) continue;
                var lines = PairLines(Analyzer.Pair(ms[i], ms[j]), xu);
                if (lines.Count > 0) rel.Add($"- **{ms[i].Short} ↔ {ms[j].Short}**: " + string.Join("; ", lines));
            }
        if (rel.Count > 0)
        {
            sb.AppendLine("## 신호 간 관계");
            foreach (var r in rel) sb.AppendLine(r);
            sb.AppendLine();
        }

        // ---- event order
        var events = new List<(double T, string Text)>();
        foreach (var m in ms)
        {
            if (m.Rise.Count + m.Fall.Count is 0 or > 12) continue;
            events.AddRange(m.Rise.Select(t => (t, $"{m.Short} 상승 (↑ {F(m.Mid, m.U)} 통과)")));
            events.AddRange(m.Fall.Select(t => (t, $"{m.Short} 하강 (↓ {F(m.Mid, m.U)} 통과)")));
        }
        if (events.Count > 0)
        {
            sb.AppendLine("## 이벤트 순서 (단발 엣지, 시간순)");
            if (m0.T0 < 0 && m0.T1 > 0) events.Add((0, "트리거 (t = 0)"));
            double? prev = null;
            foreach (var (t, text) in events.OrderBy(e => e.T))
            {
                sb.AppendLine($"- t = {F(t, xu)}: {text}" + (prev.HasValue ? $" (+{F(t - prev.Value, xu)})" : ""));
                prev = t;
            }
            sb.AppendLine();
        }

        // ---- summary of issues
        var issues = ms.SelectMany(m => m.Anomalies.Select(a => $"{m.Short}: {a}")).ToList();
        if (set.Meta is { TriggerSource: { } tsrc, TriggerLevel: double tl } &&
            ms.FirstOrDefault(m => m.W.Source.Equals(tsrc, StringComparison.OrdinalIgnoreCase)) is { } tm &&
            (tl > tm.Max || tl < tm.Min))
            issues.Add($"트리거 레벨({F(tl, tm.U)})이 트리거 소스 {tm.Short}의 신호 범위({F(tm.Min, tm.U)} ~ {F(tm.Max, tm.U)}) 밖입니다. " +
                       "트리거가 걸리지 않아 AUTO 모드로 자유 실행 중일 수 있으며, 이때 t = 0은 실제 이벤트와 무관합니다.");
        var noise = ms.Where(m => m.Kind == SignalKind.Noise).Select(m => m.Short).ToList();
        if (noise.Count > 0)
            issues.Add($"유효 신호 없음: {string.Join(", ", noise)} — 프로브 연결·신호 전원·V/div를 확인하세요.");
        sb.AppendLine("## 주의할 점 요약");
        if (issues.Count == 0) sb.AppendLine("- 자동 검사에서 클리핑·글리치·누락 펄스는 발견되지 않았습니다.");
        foreach (var s in issues) sb.AppendLine($"- {s}");
        sb.AppendLine();
        sb.AppendLine("_분석 시 각 신호를 위 라벨 이름으로 부르고, 신호별 특성 → 신호 간 관계·순서 → 이상 징후 → 결론/추가 측정 제안 순으로 정리하세요._");
        return sb.ToString();
    }

    private static void AppendChannel(StringBuilder sb, ChannelMeas m)
    {
        string u = m.U, xu = m.W.XUnit;
        sb.AppendLine($"### {m.Name} — {KindText(m.Kind)}");
        sb.AppendLine($"- 통계: 평균 {F(m.Mean, u)}, 최소 {F(m.Min, u)}, 최대 {F(m.Max, u)}, Vpp {F(m.Vpp, u)}, RMS {F(m.Rms, u)}, AC RMS {F(m.AcRms, u)}");
        switch (m.Kind)
        {
            case SignalKind.PulseTrain:
            case SignalKind.Transition:
                sb.AppendLine($"- 레벨: High {F(m.Top, u)}, Low {F(m.Base, u)}, 진폭 {F(m.Amp, u)}" +
                              (m.OvershootPct is double os ? $", 오버슈트 {P(os)}, 언더슈트 {P(m.UndershootPct ?? 0)}" : "") +
                              (m.LevelNoise > 0 ? $", 레벨 노이즈 {F(m.LevelNoise, u)} rms" : ""));
                break;
            case SignalKind.Dc:
                sb.AppendLine($"- DC {F(m.Mean, u)}, 리플/노이즈 {F(m.Vpp, u)} p-p ({(m.Mean != 0 ? P(Math.Abs(m.Vpp / m.Mean * 100)) : "-")}), {F(m.AcRms, u)} rms");
                break;
        }
        if (m.Freq is double f)
        {
            string what = m.Kind == SignalKind.Dc ? "리플 주파수" : "주파수";
            sb.AppendLine($"- {what} {F(f, "Hz")} (주기 {F(m.PeriodMean!.Value, xu)}, 범위 {F(m.PeriodMin!.Value, xu)} ~ {F(m.PeriodMax!.Value, xu)}, " +
                          $"주기 지터 {F(m.PeriodStd!.Value, xu)} rms, 측정 주기 {m.Rise.Count - 1}개)");
        }
        if (m.Duty is double d && m.Kind is SignalKind.PulseTrain)
            sb.AppendLine($"- 듀티 {P(d)} (High 폭 {F(m.PosWidth!.Value, xu)}, Low 폭 {F(m.NegWidth!.Value, xu)})");
        if (m.RiseTime.HasValue || m.FallTime.HasValue)
            sb.AppendLine("- 엣지 (10–90 %, 중앙값): " + string.Join(", ", new[]
            {
                m.RiseTime is double r ? $"상승 {F(r, xu)} (n={m.RiseTimeN})" : null,
                m.FallTime is double fl ? $"하강 {F(fl, xu)} (n={m.FallTimeN})" : null,
            }.Where(x => x != null)));
        if (m.Kind is not (SignalKind.Flat or SignalKind.Noise or SignalKind.Dc))
            sb.AppendLine($"- 엣지 수: 상승 {m.Rise.Count}, 하강 {m.Fall.Count} (기준 {F(m.Mid, u)}, 히스테리시스 ±{F(m.Hys / 2, u)})");
        if (m.Kind == SignalKind.Transition)
        {
            var edges = m.Rise.Select(t => (t, "↑")).Concat(m.Fall.Select(t => (t, "↓"))).OrderBy(e => e.t).Take(8);
            sb.AppendLine("- 전환 시점: " + string.Join(", ", edges.Select(e => $"{e.Item2} {F(e.t, xu)}")));
        }
        foreach (var a in m.Anomalies) sb.AppendLine($"- ⚠ {a}");
        foreach (var n in m.Notes) sb.AppendLine($"- 참고: {n}");
        sb.AppendLine();
    }

    private static List<string> PairLines(Analyzer.PairMeas p, string xu)
    {
        var l = new List<string>();
        string a = p.A.Short, b = p.B.Short;
        if (p.Corr is double c)
        {
            string desc = c switch
            {
                > 0.9 => "거의 같은 모양 (동상)",
                > 0.5 => "양의 상관",
                < -0.9 => "거의 반전된 모양 (상보/역상)",
                < -0.5 => "음의 상관",
                _ => "상관 약함",
            };
            l.Add($"상관계수 {c.ToString("0.00", CultureInfo.InvariantCulture)} ({desc})");
        }
        if (p.FreqRatio is double r)
        {
            if (Math.Abs(r - 1) < 0.02)
            {
                if (p.A.Kind == SignalKind.Dc) l.Add($"{a} 리플이 {b} 신호와 같은 주파수 (스위칭에 동기된 리플)");
                else if (p.B.Kind == SignalKind.Dc) l.Add($"{b} 리플이 {a} 신호와 같은 주파수 (스위칭에 동기된 리플)");
                else l.Add("같은 주파수");
            }
            else
            {
                double big = Math.Max(r, 1 / r);
                double near = Math.Round(big);
                string hint = Math.Abs(big - near) / big < 0.02 ? $" (정수배 {near:0}:1 → 분주/체배 관계 가능)" : "";
                l.Add($"주파수 비 {a}/{b} = {r.ToString("0.###", CultureInfo.InvariantCulture)}{hint}");
            }
        }
        if (p.Delay is double d)
            l.Add($"{a} 상승 → {b} 상승 지연 {F(d, xu)} (위상 {p.Phase!.Value.ToString("0.#", CultureInfo.InvariantCulture)}°, 양수면 {b} 쪽이 늦음)");
        if (p.DeadAB is double d1)
            l.Add(d1 >= 0 ? $"데드타임 {a}↓→{b}↑ {F(d1, xu)}" : $"⚠ 겹침 {a}↓ 전에 {b}↑ ({F(-d1, xu)} 동시 On — 슛스루 위험)");
        if (p.DeadBA is double d2)
            l.Add(d2 >= 0 ? $"데드타임 {b}↓→{a}↑ {F(d2, xu)}" : $"⚠ 겹침 {b}↓ 전에 {a}↑ ({F(-d2, xu)} 동시 On — 슛스루 위험)");
        if (p.FirstEdgeDelta is double fe)
            l.Add($"첫 엣지 시간차: {b} 쪽이 {a}보다 {F(Math.Abs(fe), xu)} {(fe >= 0 ? "늦음" : "빠름")}");
        return l;
    }
}
