using System.Text;

namespace Rochas.OpenCodeBridge.Web.Services;

// Gate de escopo da tool "subagent": o chamador deve dividir o trabalho em
// fases e delegar UMA fase por chamada. Repassar o enunciado integral (ou
// quase) ao subagente faz o sub repetir a tarefa inteira — sempre reprovado.
// Sem tocar prompts: é checagem mecânica sobre os argumentos da chamada.
public static class SubagentScopeGate
{
    // Prompt é "o enunciado original" quando cobre >= 70% das palavras dele
    // e tem tamanho >= 70% do original (fase de verdade é curta e específica).
    public static bool IsFullTaskPass(string? originalTask, string? prompt, double minCover = 0.7)
    {
        if (string.IsNullOrWhiteSpace(originalTask) || string.IsNullOrWhiteSpace(prompt)) return false;
        var orig = Words(originalTask);
        if (orig.Count < 8) return false;           // enunciado curto: nada a dividir
        var cand = Words(prompt);
        if (cand.Count == 0) return false;
        int hit = orig.Count(cand.Contains);
        double cover = (double)hit / orig.Count;
        double len = (double)prompt.Length / Math.Max(1, originalTask.Length);
        return cover >= minCover && len >= 0.7;
    }

    // Corretivo devolvido ao modelo quando a chamada é recusada (chega como
    // mensagem de contexto, não como system prompt).
    public static string RejectMessage(int attempt) =>
        $"[subagente {attempt}/2] recusado: o prompt repassa o enunciado original inteiro. " +
        "Divida o trabalho em fases (plano em texto) e delegue APENAS a FASE 1, com o enunciado " +
        "dela: objetivo, caminhos/arquivos e o que fazer. Cada chamada de subagent = UMA fase.";

    private static HashSet<string> Words(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w.Length >= 4) set.Add(w);          // stopwords/pontuação viram ruído
        return set;
    }
}
