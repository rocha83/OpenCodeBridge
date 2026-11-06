namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Guarda de argumentos degenerados: marcador de template dentro dos args nao
/// pode chegar ao handler (executaria lixo). Medido no E-tools-full de 2026-11-06.
/// </summary>
internal static class UnitToolArgGuardTests
{
    public static Task RejectsTemplateGarbage()
    {
        const string lixo = "{\"command\":\"config user.name \\\"data-test\\\" --local\\\",\\\"path\\\":\\\"./\\\"}<tool_call|>response:git{value:\"[stdo\"}";
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.Tools.ToolArgGuard.InvalidReason(lixo) == "<tool_call",
            "detecta `<tool_call|>` dentro dos args");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.Tools.ToolArgGuard.InvalidReason(
            "{\"command\":\"add .\",\"path\":\"./\"}") is null,
            "args validos passam intactos");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.Tools.ToolArgGuard.InvalidReason(
            "{\"content\":\"texto normal com <tag> de html\"}") is null,
            "tag html comum nao e' lixo de template");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.Tools.ToolArgGuard.InvalidReason(
            new string('a', global::Rochas.OpenCodeBridge.Core.Tools.ToolArgGuard.MaxChars + 1)) is not null,
            "argumento acima do teto e' recusado (medido: 48.936 chars no r1)");
        return Task.CompletedTask;
    }
}
