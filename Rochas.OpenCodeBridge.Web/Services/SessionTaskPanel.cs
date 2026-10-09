using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Painel de acompanhamento: deriva o estado das mini-tarefas das mensagens
// marcadas ([Orquestrador] Dividi... / [Executor i] Iniciado/Concluído).
public static class SessionTaskPanel
{
    public sealed record PanelTask(int Index, string Title, string Status);

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
            bool done = contents.Any(c => c.Contains($"[Executor {idx}] Concluído"));
            bool started = contents.Any(c => c.Contains($"[Executor {idx}] Iniciado"));
            tasks.Add(new PanelTask(idx, title, done ? "done" : started ? "running" : "pending"));
        }
        return tasks;
    }

    public static bool Synthesized(IEnumerable<(string Role, string Content)> messages, int taskCount) =>
        taskCount > 0 && messages.Any(m => m.Role == "assistant"
            && !(m.Content ?? "").StartsWith("[Orquestrador]")
            && !(m.Content ?? "").StartsWith("[Executor"));
}
