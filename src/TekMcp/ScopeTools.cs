using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TekCapture;

namespace TekMcp;

[McpServerToolType]
public static class ScopeTools
{
    private static CallToolResult Ok(string text, byte[]? png = null)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = text } };
        if (png != null) content.Add(ImageContentBlock.FromBytes(png, "image/png"));
        return new CallToolResult { Content = content };
    }

    private static CallToolResult Fail(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"오류: {ex.Message}" }],
    };

    /// <summary>MCP requests can arrive concurrently; tools share the current waveform set and the scope,
    /// so they run one at a time in arrival order.</summary>
    private static readonly object s_toolLock = new();

    internal static CallToolResult Guard(Func<CallToolResult> f)
    {
        lock (s_toolLock)
        {
            try { return f(); }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    private static List<Waveform> Select(WaveSet set, string[]? channels)
    {
        if (channels is not { Length: > 0 }) return set.Waves;
        var r = new List<Waveform>();
        foreach (var c in channels)
            r.Add(Names.Find(set.Waves, c) ?? throw new ArgumentException($"채널 '{c}'을(를) 찾지 못했습니다. 있는 채널: {string.Join(", ", set.Waves.Select(Names.Of))}"));
        return r;
    }

    // ------------------------------------------------------------------ scope

    [McpServerTool(Name = "scope_info", ReadOnly = true)]
    [Description("오실로스코프 연결 상태와 현재 설정(모델, 표시 채널, 채널 라벨, 수평/트리거/획득 설정)을 확인한다. 캡처 전에 상태를 볼 때 사용.")]
    public static CallToolResult ScopeInfo(
        [Description("VISA 리소스 문자열. 비우면 Tektronix USB 장치를 자동으로 찾는다.")] string? resource = null) => Guard(() =>
    {
        return Session.WithScope(resource, s =>
        {
            var srcs = s.DisplayedSources();
            var meta = Session.ReadMeta(s, srcs);
            var app = AppSettings.Labels;
            var sb = new StringBuilder();
            sb.AppendLine($"연결됨: {s.Resource}");
            sb.AppendLine($"IDN: {s.Idn}");
            if (meta.Horizontal is { } h)
                sb.AppendLine($"수평: {Eng.Format(h.TimePerDiv, "s")}/div, {Eng.Format(h.SampleRate, "S/s")}, 레코드 {h.RecordLength:N0} pts (전체 {Eng.Format(h.Duration, "s")})");
            if (meta.Trigger.Length > 0) sb.AppendLine($"트리거: {meta.Trigger}");
            if (meta.Acquisition.Length > 0) sb.AppendLine($"획득: {meta.Acquisition}, 상태 {(s.Query("ACQuire:STATE?").Trim() == "1" ? "RUN" : "STOP")}");
            sb.AppendLine();
            sb.AppendLine("| 채널 | 라벨 | 라벨 출처 | V/div | 기타 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var src in srcs)
            {
                string scopeLabel = "";
                string scale = "";
                if (src.StartsWith("CH"))
                {
                    try { scopeLabel = s.Query($"{src}:LABel?").Trim().Trim('"').Trim(); } catch (VisaException) { }
                    try { scale = Eng.Format(double.Parse(s.Query($"{src}:SCAle?"), CultureInfo.InvariantCulture), "V"); } catch (Exception ex) when (ex is VisaException or FormatException) { }
                }
                var (label, from) = app.TryGetValue(src, out var l) && l.Length > 0 ? (l, "TekCapture 앱")
                    : scopeLabel.Length > 0 ? (scopeLabel, "스코프") : ("(없음)", "-");
                sb.AppendLine($"| {src} | {label} | {from} | {scale} | {meta.Channel.GetValueOrDefault(src, "")} |");
            }
            if (srcs.Count == 0) sb.AppendLine("(표시 중인 채널 없음)");
            return Ok(sb.ToString());
        });
    });

    [McpServerTool(Name = "capture_screen")]
    [Description("오실로스코프 화면을 PNG로 캡처해 이미지로 돌려준다 (전면 패널 하드카피와 같음). 채널 라벨 표도 함께 준다. 화면의 측정값·커서·설정을 그대로 읽어야 할 때 사용.")]
    public static CallToolResult CaptureScreen(
        [Description("true면 흰 배경(Ink Saver)으로 캡처")] bool ink_saver = false,
        [Description("저장 폴더에 PNG 파일로도 저장")] bool save = true,
        [Description("저장 폴더. 비우면 TekCapture 앱 설정 폴더(기본 문서\\TekCapture)")] string? folder = null,
        [Description("VISA 리소스. 비우면 자동")] string? resource = null) => Guard(() =>
    {
        var (png, model, srcs, scopeLabels) = Session.WithScope(resource, s =>
        {
            var png = s.CaptureScreen(ink_saver);
            var srcs = s.DisplayedSources();
            var lbl = new Dictionary<string, string>();
            foreach (var src in srcs.Where(x => x.StartsWith("CH")))
                try { lbl[src] = s.Query($"{src}:LABel?").Trim().Trim('"').Trim(); } catch (VisaException) { }
            return (png, s.Model, srcs, lbl);
        });

        var sb = new StringBuilder();
        sb.AppendLine($"{model} 화면 캡처 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}, {png.Length:N0} bytes)");
        if (save)
        {
            var path = Path.Combine(Session.ResolveFolder(folder), Session.Stamp(model) + "_screen.png");
            File.WriteAllBytes(path, png);
            sb.AppendLine($"저장: {path}");
        }
        var app = AppSettings.Labels;
        sb.AppendLine("표시 중인 채널과 이름:");
        foreach (var src in srcs)
        {
            var name = app.GetValueOrDefault(src) is { Length: > 0 } a ? a : scopeLabels.GetValueOrDefault(src, "");
            sb.AppendLine($"- {src}: {(name.Length > 0 ? name : "(라벨 없음)")}" +
                          (name.Length > 0 && scopeLabels.GetValueOrDefault(src, "") != name ? " — 스코프 화면에는 표시되지 않음 (set_channel_labels로 스코프에도 쓸 수 있음)" : ""));
        }
        sb.AppendLine("화면을 설명할 때 채널 색(CH1 노랑, CH2 하늘, CH3 분홍, CH4 초록)으로 구분하고, 위 라벨 이름으로 부르세요.");
        return Ok(sb.ToString(), png);
    });

    [McpServerTool(Name = "capture_waveforms")]
    [Description("스코프에서 파형 데이터를 받아 채널별 자동 측정(레벨, 주파수, 듀티, 상승/하강시간, 오버슈트, 클리핑·글리치 검사)과 채널 간 관계(상관, 지연/위상, 데드타임, 이벤트 순서)를 라벨 이름으로 정리한 보고서와 그래프 이미지를 돌려준다. 이후 analyze_window·plot_waveforms·get_waveform_samples로 세부를 볼 수 있다.")]
    public static CallToolResult CaptureWaveforms(
        [Description("읽을 채널 (예: [\"CH1\",\"CH3\"]). 비우면 화면에 표시 중인 채널 전부")] string[]? channels = null,
        [Description("스코프가 RUN 중이면 잠시 Stop해 모든 채널을 같은 획득에서 읽고 다시 Run으로 되돌림")] bool sync_stop = true,
        [Description("CSV로 저장 (라벨이 헤더에 들어감)")] bool save_csv = true,
        [Description("그래프 이미지 포함")] bool include_plot = true,
        [Description("저장 폴더. 비우면 TekCapture 앱 설정 폴더")] string? folder = null,
        [Description("VISA 리소스. 비우면 자동")] string? resource = null) => Guard(() =>
    {
        var set = Session.Capture(resource, channels, sync_stop);
        if (save_csv)
        {
            var path = Path.Combine(Session.ResolveFolder(folder), Session.Stamp(Session.ModelOf(set)) + "_wave.csv");
            CsvExport.Write(path, set.Waves);
            set.CsvPath = path;
        }
        Session.Current = set;
        var text = Report.Build(set, set.Waves, null, null);
        return Ok(text, include_plot ? Plot.Render(set.Waves, null, null) : null);
    });

    // ------------------------------------------------------------------ files

    [McpServerTool(Name = "load_waveform_csv", ReadOnly = true)]
    [Description("TekCapture가 저장한 파형 CSV를 불러와 capture_waveforms와 같은 보고서와 그래프를 돌려준다. 라벨은 CSV 헤더에서 읽는다. path를 비우면 저장 폴더의 가장 최근 *_wave.csv를 연다.")]
    public static CallToolResult LoadCsv(
        [Description("CSV 경로. 비우면 가장 최근 파일")] string? path = null,
        [Description("그래프 이미지 포함")] bool include_plot = true) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = AppSettings.Folder;
            path = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("*.csv").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
            if (path == null) throw new FileNotFoundException($"'{dir}'에 CSV 파일이 없습니다.");
        }
        var waves = CsvImport.Read(path);
        var set = new WaveSet { Waves = waves, Origin = "CSV 파일", Time = File.GetLastWriteTime(path), CsvPath = path };
        Session.Current = set;
        var text = Report.Build(set, waves, null, null);
        return Ok(text, include_plot ? Plot.Render(waves, null, null) : null);
    });

    [McpServerTool(Name = "list_captures", ReadOnly = true)]
    [Description("저장 폴더의 캡처 파일(화면 PNG, 파형 CSV)을 최신순으로 나열한다. CSV는 헤더의 채널·라벨도 보여준다.")]
    public static CallToolResult ListCaptures(
        [Description("최대 개수")] int limit = 20,
        [Description("폴더. 비우면 TekCapture 앱 설정 폴더")] string? folder = null) => Guard(() =>
    {
        var dir = string.IsNullOrWhiteSpace(folder) ? AppSettings.Folder : folder;
        if (!Directory.Exists(dir)) return Ok($"폴더가 없습니다: {dir}");
        var files = new DirectoryInfo(dir).GetFiles()
            .Where(f => f.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(Math.Clamp(limit, 1, 200)).ToList();
        var sb = new StringBuilder($"폴더: {dir}\n");
        foreach (var f in files)
        {
            sb.Append($"- {f.Name}  ({f.LastWriteTime:yyyy-MM-dd HH:mm:ss}, {f.Length / 1024.0:N0} KB)");
            if (f.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var sr = new StreamReader(f.FullName);
                    sb.Append("  열: " + sr.ReadLine());
                }
                catch { }
            }
            sb.AppendLine();
        }
        if (files.Count == 0) sb.AppendLine("(캡처 파일 없음)");
        return Ok(sb.ToString());
    });

    // ------------------------------------------------------------------ labels

    [McpServerTool(Name = "set_channel_labels")]
    [Description("채널에 신호 이름(라벨)을 붙인다 (예: {\"CH1\":\"VIN\",\"CH2\":\"PWM_H\"}). 현재 분석 중인 파형에 바로 적용되고, TekCapture 앱 설정에 저장되며, 원하면 스코프 화면 라벨(CHx:LABel)에도 쓴다. 이후 보고서와 그래프는 이 이름을 쓴다.")]
    public static CallToolResult SetLabels(
        [Description("채널 → 라벨. 키는 CH1~CH4/MATH, 값을 빈 문자열로 주면 라벨 삭제")] Dictionary<string, string> labels,
        [Description("스코프의 CHx:LABel에도 기록해 화면 캡처에 이름이 보이게 함")] bool write_to_scope = true,
        [Description("TekCapture 앱 설정(settings.json)에 저장")] bool save_to_app = true,
        [Description("VISA 리소스. 비우면 자동")] string? resource = null) => Guard(() =>
    {
        var norm = labels.ToDictionary(kv => int.TryParse(kv.Key.Trim(), out _) ? "CH" + kv.Key.Trim() : kv.Key.Trim().ToUpperInvariant(), kv => (kv.Value ?? "").Trim());
        var sb = new StringBuilder();
        foreach (var (k, v) in norm) Session.LabelOverrides[k] = v;
        if (Session.Current is { } cur)
            foreach (var w in cur.Waves)
                if (norm.TryGetValue(w.Source, out var l)) w.Label = l;
        if (save_to_app)
        {
            AppSettings.SaveLabels(norm);
            sb.AppendLine($"TekCapture 앱 설정에 저장: {AppSettings.Path}");
        }
        if (write_to_scope)
        {
            try
            {
                var skipped = new List<string>();
                Session.WithScope(resource, s =>
                {
                    foreach (var (src, l) in norm.Where(kv => kv.Key.StartsWith("CH")))
                    {
                        // scope labels are ASCII only, max 30 chars
                        if (l.Any(ch => ch > 126 || ch < 32)) { skipped.Add(src); continue; }
                        var t = l.Replace("\"", "");
                        s.Write($"{src}:LABel \"{(t.Length > 30 ? t[..30] : t)}\"");
                    }
                    return 0;
                });
                sb.AppendLine("스코프 채널 라벨에도 기록했습니다.");
                if (skipped.Count > 0)
                    sb.AppendLine($"스코프는 영문/숫자 라벨만 표시할 수 있어 {string.Join(", ", skipped)}은(는) 스코프에 쓰지 않았습니다 (분석·앱 라벨은 적용됨).");
            }
            catch (Exception ex) { sb.AppendLine($"스코프 라벨 기록 실패 (앱/분석 라벨은 적용됨): {ex.Message}"); }
        }
        sb.AppendLine("적용된 라벨: " + string.Join(", ", norm.Select(kv => $"{kv.Key} = {(kv.Value.Length > 0 ? kv.Value : "(삭제)")}")));
        return Ok(sb.ToString());
    });

    // ------------------------------------------------------------------ zoom / detail on the current set

    [McpServerTool(Name = "analyze_window", ReadOnly = true)]
    [Description("현재 파형(마지막 캡처/불러온 CSV)의 특정 시간 구간만 다시 분석한다. 기동 구간, 이벤트 주변 등을 확대해서 측정할 때 사용. 시간은 초 단위(트리거 = 0).")]
    public static CallToolResult AnalyzeWindow(
        [Description("시작 시간 [s] (예: -0.0001). 비우면 처음부터")] double? t_start = null,
        [Description("끝 시간 [s]. 비우면 끝까지")] double? t_end = null,
        [Description("분석할 채널 (라벨이나 CH 번호). 비우면 전부")] string[]? channels = null,
        [Description("그래프 이미지 포함")] bool include_plot = true) => Guard(() =>
    {
        var set = Session.RequireCurrent();
        var waves = Select(set, channels);
        var text = Report.Build(set, waves, t_start, t_end, "구간 분석");
        return Ok(text, include_plot ? Plot.Render(waves, t_start, t_end) : null);
    });

    [McpServerTool(Name = "plot_waveforms", ReadOnly = true)]
    [Description("현재 파형을 그래프 이미지로 그린다. 채널마다 라벨 이름이 표시된다. stacked(채널별 분리) 또는 overlay(겹쳐 그리기).")]
    public static CallToolResult PlotWaveforms(
        [Description("시작 시간 [s]. 비우면 처음부터")] double? t_start = null,
        [Description("끝 시간 [s]. 비우면 끝까지")] double? t_end = null,
        [Description("그릴 채널 (라벨이나 CH 번호). 비우면 전부")] string[]? channels = null,
        [Description("true면 한 격자에 겹쳐 그림 (타이밍 비교용)")] bool overlay = false) => Guard(() =>
    {
        var set = Session.RequireCurrent();
        var waves = Select(set, channels);
        var png = Plot.Render(waves, t_start, t_end, overlay);
        return Ok($"그래프: {string.Join(", ", waves.Select(Names.Of))}, {Eng.Format(t_start ?? waves.Min(w => w.TStart), "s")} ~ {Eng.Format(t_end ?? waves.Max(w => w.TEnd), "s")}", png);
    });

    [McpServerTool(Name = "get_waveform_samples", ReadOnly = true)]
    [Description("현재 파형의 한 채널에서 시간 구간의 샘플 값을 표로 돌려준다. 구간 샘플이 max_points보다 많으면 구간별 최소/최대/평균으로 줄인다. 정확한 전압 값이나 엣지 모양을 숫자로 확인할 때 사용.")]
    public static CallToolResult GetSamples(
        [Description("채널 (라벨 예: \"VIN\" 또는 \"CH1\")")] string channel,
        [Description("시작 시간 [s]. 비우면 처음부터")] double? t_start = null,
        [Description("끝 시간 [s]. 비우면 끝까지")] double? t_end = null,
        [Description("최대 행 수 (10~2000)")] int max_points = 200) => Guard(() =>
    {
        var set = Session.RequireCurrent();
        var w = Names.Find(set.Waves, channel) ?? throw new ArgumentException($"채널 '{channel}'을(를) 찾지 못했습니다. 있는 채널: {string.Join(", ", set.Waves.Select(Names.Of))}");
        int a = Math.Clamp(t_start.HasValue ? w.IndexAt(t_start.Value) : 0, 0, w.Length - 1);
        int b = Math.Clamp(t_end.HasValue ? w.IndexAt(t_end.Value) : w.Length - 1, 0, w.Length - 1);
        if (b < a) (a, b) = (b, a);
        int n = b - a + 1;
        int rows = Math.Clamp(max_points, 10, 2000);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"{Names.Of(w)} ({w.YUnit}), {Eng.Format(w.Time(a), w.XUnit)} ~ {Eng.Format(w.Time(b), w.XUnit)}, 원본 {n:N0} 샘플");
        if (n <= rows)
        {
            sb.AppendLine($"time_s,{w.YUnit}");
            for (int i = a; i <= b; i++) sb.AppendLine($"{w.Time(i).ToString("G9", inv)},{w.Y[i].ToString("G6", inv)}");
        }
        else
        {
            sb.AppendLine($"{rows}개 구간으로 줄임 (구간당 약 {n / (double)rows:F0} 샘플)");
            sb.AppendLine($"t_start_s,t_end_s,min,max,mean");
            for (int k = 0; k < rows; k++)
            {
                int ia = a + (int)((long)n * k / rows), ib = a + (int)((long)n * (k + 1) / rows) - 1;
                float mn = float.MaxValue, mx = float.MinValue; double s = 0;
                for (int i = ia; i <= ib; i++) { var v = w.Y[i]; if (v < mn) mn = v; if (v > mx) mx = v; s += v; }
                sb.AppendLine($"{w.Time(ia).ToString("G9", inv)},{w.Time(ib).ToString("G9", inv)},{mn.ToString("G6", inv)},{mx.ToString("G6", inv)},{(s / (ib - ia + 1)).ToString("G6", inv)}");
            }
        }
        return Ok(sb.ToString());
    });

    // ------------------------------------------------------------------ acquisition control

    [McpServerTool(Name = "acquisition_control")]
    [Description("스코프 획득 제어: run(연속), stop(정지), single(단발 트리거 1회). single 후에는 트리거가 걸릴 때까지 기다린 다음 capture_waveforms를 호출하면 된다.")]
    public static CallToolResult Acquisition(
        [Description("run | stop | single")] string action,
        [Description("VISA 리소스. 비우면 자동")] string? resource = null) => Guard(() =>
    {
        var msg = Session.WithScope(resource, s =>
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "run": s.Run(); return "RUN (연속 획득)";
                case "stop": s.Stop(); return "STOP";
                case "single": s.Single(); return "SINGLE (트리거 대기 중 — 트리거 후 자동 정지)";
                default: throw new ArgumentException("action은 run, stop, single 중 하나");
            }
        });
        return Ok($"획득 상태: {msg}");
    });
}
