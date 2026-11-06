namespace Rochas.OpenCodeBridge.Core.Tools;

// Resultado de uma execução de tool (objeto de valor do domínio).
public sealed class ToolResult
{
    // Marcador canonico de falha em historico de chat: o executor (fonte da
    // verdade semantica, ToolResult.Success) carimba o resultado com este prefixo
    // para que o breaker (que so ve texto) detecte a falha sem sniffar vocabulario
    // de ecossistema. Manter sincronizado com ToolsAntiRepeat.LooksLikeFailure.
    public const string HistoryFailurePrefix = "ERRO: ";

    public bool Success { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";

    public static ToolResult Ok(string output) => new() { Success = true, Output = output };
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };

    /// <summary>
    /// Renderiza o resultado no formato canonico de historico de chat. A falha e'
    /// marcada pelo prefixo <see cref="HistoryFailurePrefix"/> (derivado de
    /// <see cref="Success"/>, nao do texto), para que o breaker detecte a falha
    /// de forma deterministica em vez de inferir do vocabulario da ferramenta.
    /// </summary>
    public string ToHistoryContent()
        => Success ? (Output ?? "") : HistoryFailurePrefix + (Error ?? "");

    /// <summary>Forma estatica para clientes que recebem ok/output/error separados.</summary>
    public static string FormatForHistory(bool success, string? output, string? error)
        => success ? (output ?? "") : HistoryFailurePrefix + (error ?? "");
}
