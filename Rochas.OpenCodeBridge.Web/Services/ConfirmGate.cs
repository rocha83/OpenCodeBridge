// Portao de confirmacao: mutacoes pedem "pode fazer" do usuario.
// Sem infra de approve async: a ultima mensagem user carrega o aval;
// sem aval, a call e negada com pedido claro (o modelo pergunta e retry).
// /Chat/Tool (clique manual) nao passa por aqui: clique = aval implicito.
namespace Rochas.OpenCodeBridge.Web.Services;

internal static class ConfirmGate
{
    // Palavras de aval (explicitas; "sim"/"ok" sozinhos nao valem).
    private static readonly string[] Approvals =
    {
        "pode sim", "pode fazer", "autorizo", "aprovado", "confirmo",
        "pode executar", "vai em frente", "manda ver", "prossiga",
        "pode continuar", "pode commitar", "pode dar push",
    };

    /// <summary>Call mutante que exige aval? (push via shell, write, edit).</summary>
    internal static bool NeedsConfirmation(string name, string args)
    {
        if (name is "write" or "edit") return true;
        if (name is "shell" or "bash" or "sh" or "exec" or "proc")
            return IsGitPush(ShellInspector.ExtractCommand(args ?? ""));
        return false;
    }

    /// <summary>Detecta git push (fora de aspas: -m "push it" nao conta).</summary>
    internal static bool IsGitPush(string command)
    {
        string bare = System.Text.RegularExpressions.Regex.Replace(command ?? "", @"(""[^""]*""|'[^']*')", " ");
        return System.Text.RegularExpressions.Regex.IsMatch(bare, @"\bgit\b[\s\S]*\bpush\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Ultima mensagem user trouxe aval explicito?</summary>
    internal static bool ApprovedBy(string? lastUser) =>
        !string.IsNullOrWhiteSpace(lastUser) && Approvals.Any(a =>
            (lastUser ?? "").Contains(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ultimo texto user em messages (formato chat).</summary>
    internal static string? LastUserText(System.Text.Json.Nodes.JsonArray messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
            if (messages[i] is System.Text.Json.Nodes.JsonObject o
                && o["role"]?.GetValue<string>() == "user"
                && o["content"]?.GetValue<string>() is string t && !string.IsNullOrWhiteSpace(t))
                return t;
        return null;
    }

    /// <summary>Ultimo texto user no input (formato Responses).</summary>
    internal static string? LastUserInputText(System.Text.Json.Nodes.JsonArray input)
    {
        for (int i = input.Count - 1; i >= 0; i--)
        {
            if (input[i] is not System.Text.Json.Nodes.JsonObject o) continue;
            if (o["type"]?.GetValue<string>() != "message") continue;
            if (o["role"]?.GetValue<string>() != "user") continue;
            if (o["content"] is not System.Text.Json.Nodes.JsonArray parts) continue;
            foreach (var p in parts)
                if (p is System.Text.Json.Nodes.JsonObject po
                    && po["text"] is System.Text.Json.Nodes.JsonValue tv
                    && tv.TryGetValue<string>(out string? t) && !string.IsNullOrWhiteSpace(t))
                    return t;
        }
        return null;
    }

    internal const string DenyMessage =
        "Aguardando confirmacao do usuario: esta acao (push/escrita) exige aval explicito. Descreva o que vai fazer e peca 'pode fazer'; sem o aval, nao execute.";
}
