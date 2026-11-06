using System;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Gate generico da tool shell. Shell e' aberto: a politica e' BLACKLIST (nega
/// o risco, deixa o resto), nao allowlist de argv. Depois de executar, comando
/// ou opcao inexistente (exit 127/129) vira a MESMA elucidação padronizada —
/// exit 128 fica de fora: e' erro de uso real, nao de vocabulario.
/// </summary>
public static class ShellGate
{
    public enum Verdict { Allow, Deny, Invalid }

    /// <summary>Pre-execucao: null = liberado; texto = motivo (blacklist).</summary>
    public static (Verdict Verdict, string? Reason) CheckCommand(string command)
    {
        string? why = ShellInspector.Validate(command);
        return why is null ? (Verdict.Allow, null) : (Verdict.Deny, why);
    }

    /// <summary>Recusa no envelope unico (blacklist do inspetor).</summary>
    public static ToolResult RefuseReason(string reason) =>
        ToolFeedback.Refuse(reason, "blacklist do inspetor de shell (comando/perigo)",
            "reformule sem o token/padrao negado ou use a tool dedicada (read/write/edit/git)");

    /// <summary>
    /// Pos-execucao: 127 (comando nao encontrado) e 129 (opcao desconhecida)
    /// viram ERRO/CAUSA/CORRIJA/RETRY. Qualquer outro erro passa intacto.
    /// </summary>
    public static ToolResult ExplainInvalidUsage(ToolResult result, string command)
    {
        if (result.Success) return result;
        int code = ExitCode(result.Error ?? "");
        if (code == 127)
        {
            string bin = ShellInspector.FirstToken(command);
            return ToolFeedback.InvalidUsage(
                $"comando nao encontrado: '{bin}'",
                "o binario nao existe no PATH desta maquina",
                "use um binario instalado (confira com `which " + bin + "`) ou resolva com read/write/edit; " +
                "se for git, use a tool git (formas no help do schema)");
        }
        if (code == 129)
            return ToolFeedback.InvalidUsage(
                $"opcao invalida em: '{command}'",
                "o programa nao conhece essa opcao (vocabulario inventado)",
                "repita a MESMA acao com a forma do help da tool (schema); no git use a tool git");
        return result;
    }

    /// <summary>Codigo "exit NNN" da saida de erro (0 = nao encontrado).</summary>
    public static int ExitCode(string error)
    {
        var m = Regex.Match(error ?? "", @"\bexit\s+(\d{1,3})\b");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
