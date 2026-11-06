namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Envelope unico de recusa/explicacao de tool. Antes cada gate falava de um
/// jeito (git mandava a receita, shell mandava stderr cru, args mandavam
/// "invalidos"); o modelo pequeno so corrige em 1 turno quando o erro tem
/// SEMPRE a mesma forma. DRY: um formatador, usado por todos os gates.
/// </summary>
public static class ToolFeedback
{
    /// <summary>Monta ERRO/CAUSA/CORRIJA/RETRY (a forma que o modelo le).</summary>
    /// <param name="error">O que falhou (comando/token/args).</param>
    /// <param name="cause">Uma linha objetiva da causa.</param>
    /// <param name="fix">Forma exata ou alternativa a usar.</param>
    /// <param name="retry">true = vale repetir a call corrigida.</param>
    public static string Format(string error, string cause, string fix, bool retry = true) =>
        "ERRO: " + error + "\nCAUSA: " + cause + "\nCORRIJA: " + fix + "\nRETRY: " + (retry ? "sim" : "nao");

    /// <summary>Recusa de politica (blacklist) no envelope.</summary>
    public static ToolResult Refuse(string error, string cause, string fix, bool retry = true) =>
        ToolResult.Fail(Format(error, cause, fix, retry));

    /// <summary>Forma/uso invalido (git, args, comando inexistente).</summary>
    public static ToolResult InvalidUsage(string error, string cause, string fix) =>
        ToolResult.Fail(Format(error, cause, fix));

    /// <summary>true = o texto ja esta no envelope (usado pelos units).</summary>
    public static bool IsEnvelope(string text) =>
        text.Contains("\nERRO: ", System.StringComparison.Ordinal)
        && text.Contains("\nCAUSA: ", System.StringComparison.Ordinal)
        && text.Contains("\nCORRIJA: ", System.StringComparison.Ordinal)
        && text.Contains("\nRETRY: ", System.StringComparison.Ordinal)
        || (text.StartsWith("ERRO: ", System.StringComparison.Ordinal)
            && text.Contains("\nCAUSA: ", System.StringComparison.Ordinal)
            && text.Contains("\nCORRIJA: ", System.StringComparison.Ordinal)
            && text.Contains("\nRETRY: ", System.StringComparison.Ordinal));
}
