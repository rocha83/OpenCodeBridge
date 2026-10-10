using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Painel de acompanhamento: deriva o estado das mini-tarefas das mensagens
// marcadas ([Orquestrador] Dividi... / [Executor i] Iniciado/Concluído/Executou).
public static class SessionTaskPanel
{
    public sealed record PanelTask(int Index, string Title, string Status, double EtaMin,
        string PromptSummary, List<string> ToolCalls);

    // Remove sufixos de exibição ("(~N min)", "— enunciado: ...").
    public static string CleanTitle(string raw)
    {
        string rest = raw.Trim();
        int sep = rest.IndexOf("— enunciado:", StringComparison.Ordinal);
        if (sep >= 0) rest = rest[..sep].Trim();
        var em = Regex.Match(rest, @"\(~([\d.,]+)\s*min\)\s*$");
        if (em.Success) rest = rest[..em.Index].Trim();
        return rest;
    }

    public static List<PanelTask> Parse(IEnumerable<(string Role, string Content)> messages)
    {
        var contents = messages.Select(m => m.Content ?? "").ToList();
        var decomp = contents.LastOrDefault(c => c.Contains("[Orquestrador] Dividi em"));
        var tasks = new List<PanelTask>();
        if (decomp is null) return tasks;
        foreach (string line in decomp.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^(\d+)\.\s*(.+)$");
            if (!m.Success) continue;
            int idx = int.Parse(m.Groups[1].Value);
            string raw = m.Groups[2].Value.Trim();
            // Resumo do enunciado: sufixo "— enunciado: ..." (formato novo; mensagens
            // antigas não têm e seguem com resumo vazio).
            string summary = "";
            int sep = raw.IndexOf("— enunciado:", StringComparison.Ordinal);
            if (sep >= 0) summary = raw[(sep + "— enunciado:".Length)..].Trim();
            string title = CleanTitle(raw);
            string noSummary = sep >= 0 ? raw[..sep].Trim() : raw;
            double eta = 0;
            var em = Regex.Match(noSummary, @"\(~([\d.,]+)\s*min\)\s*$");
            if (em.Success)
            {
                double.TryParse(em.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out eta);
            }
            bool done = contents.Any(c => c.Contains($"[Executor {idx}] Concluído"));
            bool started = contents.Any(c => c.Contains($"[Executor {idx}] Iniciado"));
            var tools = contents
                .SelectMany(c => Regex.Matches(c, $@"\[Executor {idx}\] Executou (\S+?)[\s:]")
                    .Select(mm => mm.Groups[1].Value.TrimEnd(':')))
                .Distinct()
                .ToList();
            tasks.Add(new PanelTask(idx, title, done ? "done" : started ? "running" : "pending",
                eta, summary, tools));
        }
        return tasks;
    }

    public static bool Synthesized(IEnumerable<(string Role, string Content)> messages, int taskCount) =>
        taskCount > 0 && messages.Any(m => m.Role == "assistant"
            && !(m.Content ?? "").StartsWith("[Orquestrador]")
            && !(m.Content ?? "").StartsWith("[Executor"));
}
