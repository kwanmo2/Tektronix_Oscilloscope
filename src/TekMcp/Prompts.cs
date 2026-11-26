using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TekMcp;

[McpServerPromptType]
public static class Prompts
{
    [McpServerPrompt(Name = "analyze_waveforms")]
    [Description("스코프(또는 최근 CSV)의 파형을 읽어 라벨 이름 기준으로 종합 분석한다.")]
    public static IEnumerable<PromptMessage> AnalyzeWaveforms(
        [Description("live = 스코프에서 지금 캡처, file = 가장 최근 저장된 CSV")] string source = "live",
        [Description("회로·측정 맥락 (예: \"12V→3.3V 벅 컨버터 기동, CH1=VIN CH2=SW CH3=VOUT\")")] string? context = null)
    {
        var fetch = source.Trim().Equals("file", StringComparison.OrdinalIgnoreCase)
            ? "load_waveform_csv를 path 없이 호출해 가장 최근 저장된 파형을 불러오세요."
            : "capture_waveforms를 호출해 스코프에서 지금 파형을 받으세요. 화면에 표시된 측정값·커서도 필요하면 capture_screen도 호출하세요.";
        var text = $"""
            {fetch}

            그다음 보고서와 그래프를 바탕으로 종합 분석을 한국어로 써 주세요.
            {(string.IsNullOrWhiteSpace(context) ? "" : $"측정 맥락: {context}\n")}
            규칙:
            - 각 신호는 반드시 라벨 이름으로 부르세요 (예: "VIN", "PWM_H"). 채널 번호는 처음 한 번만 괄호로 붙입니다.
            - 라벨이 없는 채널이 있으면 분석 전에 무슨 신호인지 물어보고, 답을 받으면 set_channel_labels로 붙인 뒤 진행하세요. 맥락에서 이름이 분명하면 바로 붙여도 됩니다.

            구성:
            1. 요약 — 이 캡처가 보여 주는 동작을 2~3문장으로.
            2. 신호별 특성 — 라벨마다 레벨, 주파수/주기, 듀티, 엣지 속도, 리플/노이즈를 수치와 함께.
            3. 신호 간 관계 — 지연·위상·데드타임·인과 순서(무엇이 먼저 변하고 무엇이 따라오는지)를 라벨 이름으로 설명.
            4. 이상 징후 — 글리치, 누락 펄스, 오버슈트/링잉, 클리핑, 해상도·샘플레이트 부족. 각각 심각도와 가능한 원인.
            5. 결론과 다음 측정 제안 — 확인이 더 필요한 부분은 analyze_window / plot_waveforms로 확대해서 직접 확인한 뒤 쓰고, 스코프 설정 변경(V/div, time/div, 트리거)이 필요하면 구체적으로 제안.
            """;
        return [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }];
    }
}
