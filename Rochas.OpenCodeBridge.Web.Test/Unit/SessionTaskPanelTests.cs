// Testes unitários do painel de tarefas (derivado das mensagens marcadas).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class SessionTaskPanelTests
{
    private static int Failures;

    internal static int Run()
    {
        Empty();
        Mixed();
        Synthesized();

        System.Console.WriteLine($"=== TaskPanel Unit: {3 - Failures}/3 PASS, {Failures} FAIL ===");
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void Empty()
    {
        var tasks = SessionTaskPanel.Parse(new[] { ("user", "ola") });
        Check(tasks.Count == 0, "U-task-empty");
    }

    private static void Mixed()
    {
        var msgs = new[]
        {
            ("user", "faça"),
            ("assistant", "[Orquestrador] Dividi em 2 tarefa(s):\n1. Tarefa A (~3 min)\n2. Tarefa B"),
            ("assistant", "[Executor 1] Iniciado: Tarefa A"),
            ("assistant", "[Executor 1] Concluído: Tarefa A\nok"),
        };
        var tasks = SessionTaskPanel.Parse(msgs);
        Check(tasks.Count == 2 && tasks[0].Status == "done" && tasks[1].Status == "pending"
            && tasks[0].Title == "Tarefa A" && tasks[0].EtaMin == 3, "U-task-mixed");
    }

    private static void Synthesized()
    {
        var msgs = new[]
        {
            ("assistant", "[Orquestrador] Dividi em 1 tarefa(s):\n1. Tarefa A"),
            ("assistant", "resposta final"),
        };
        Check(SessionTaskPanel.Synthesized(msgs, 1), "U-task-synthesized");
    }
}
