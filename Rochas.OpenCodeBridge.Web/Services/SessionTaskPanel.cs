using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Painel de acompanhamento: deriva o estado das mini-tarefas das mensagens
// marcadas ([Orquestrador] Dividi... / [Executor i] Iniciado/Concluído).
public static class SessionTaskPanel
{
    public sealed record PanelTask(int Index, string Title, string Status, double EtaMin);

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
            string title = m.Groups[2].Value.Trim();
            double eta = 0;
            var em = Regex.Match(title, @"\(~([\d.,]+)\s*min\)\s*$");
            if (em.Success)
            {
                double.TryParse(em.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out eta);
                title = title[..em.Index].Trim();
            }
            bool done = contents.Any(c => c.Contains($"[Executor {idx}] Concluído"));
            bool started = contents.Any(c => c.Contains($"[Executor {idx}] Iniciado"));
            tasks.Add(new PanelTask(idx, title, done ? "done" : started ? "running" : "pending", eta));
        }
        return tasks;
    }

    public static bool Synthesized(IEnumerable<(string Role, string Content)> messages, int taskCount) =>
        taskCount > 0 && messages.Any(m => m.Role == "assistant"
            && !(m.Content ?? "").StartsWith("[Orquestrador]")
            && !(m.Content ?? "").StartsWith("[Executor"));
}
