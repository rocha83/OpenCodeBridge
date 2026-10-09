namespace Rochas.OpenCodeBridge.Web.Services;

// Resultado de uma execução de tool (objeto de valor do domínio).
public sealed class ToolResult
{
    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";

    public static ToolResult Ok(string output) => new() { Success = true, Output = output };
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };
}
