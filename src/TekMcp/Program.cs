using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TekMcp;

internal static class Program
{
    public const string Instructions = """
        Tektronix DPO/MSO4000 계열 오실로스코프에 USB(VISA)로 연결해 화면 캡처, 파형 데이터 캡처, 자동 측정, 파형 분석을 하는 서버.

        신호 이름(라벨) 규칙:
        - 각 채널에는 라벨(예: CH1 = "VIN", CH2 = "PWM_H")이 붙을 수 있다. 라벨은 TekCapture 앱에서 붙인 값 → 스코프의 CHx:LABel → CSV 헤더 순으로 정해진다.
        - 분석 결과를 설명할 때는 반드시 라벨 이름으로 신호를 부른다 (예: "VIN이 3.3 V에 도달한 뒤 120 µs 후 EN이 상승"). 채널 번호는 괄호 안에 보조로만 쓴다.
        - 라벨이 없는 채널은 채널 번호로 부르고, 사용자에게 이름을 물어 set_channel_labels로 붙이도록 제안한다.

        분석 방법:
        - capture_waveforms(스코프에서 직접) 또는 load_waveform_csv(저장된 파일)로 데이터를 읽으면 채널별 측정과 채널 간 관계(지연·위상·데드타임·상관·이벤트 순서)가 담긴 보고서와 그래프 이미지가 돌아온다.
        - 종합 분석은 ① 신호별 특성 ② 신호 간 관계와 순서 ③ 이상 징후(글리치, 누락 펄스, 오버슈트, 클리핑, 해상도 부족) ④ 결론과 추가 측정 제안 순으로 정리한다.
        - 세부 구간이 궁금하면 analyze_window, plot_waveforms(t_start/t_end), get_waveform_samples로 확대해서 본다.
        - 두 시점 사이 시간차(지연, 펄스 폭, 상승 구간 등)는 measure_ticks로 잰다. 인자를 비우면 사용자가 TekCapture 뷰어에서 놓은 틱 A/B를 쓰고, "VIN rise", "SW fall 2"처럼 엣지로 지정할 수도 있다. "틱", "커서", "A–B"라는 말이 나오면 이 도구를 먼저 쓴다.
        - 화면 그대로가 필요하면 capture_screen을 쓴다.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--selftest")
            return SelfTest.Run(args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "TekMcpSelfTest"), args.Length >= 3 ? args[2] : null);

        var builder = Host.CreateApplicationBuilder(args);
        // stdout은 MCP 프로토콜 전용 → 로그는 모두 stderr로
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "tekscope", Version = "1.0.0" };
                o.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithPromptsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }
}
