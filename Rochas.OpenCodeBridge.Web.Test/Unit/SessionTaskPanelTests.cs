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
        SummaryAndTools();
        LegacyFormat();
        Synthesized();

        System.Console.WriteLine($"=== TaskPanel Unit: {5 - Failures}/5 PASS, {Failures} FAIL ===");
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

    private static void SummaryAndTools()
    {
        var msgs = new[]
        {
            ("user", "faça"),
            ("assistant", "[Orquestrador] Dividi em 2 tarefa(s):\n1. Tarefa A (~3 min) — enunciado: crie a classe X com propósito Y\n2. Tarefa B — enunciado: compile o projeto"),
            ("assistant", "[Executor 1] Iniciado: Tarefa A"),
            ("assistant", "[Executor 1] Executou shell: ok"),
            ("assistant", "[Executor 1] Executou read: ok"),
            ("assistant", "[Executor 1] Concluído: Tarefa A\nok"),
        };
        var tasks = SessionTaskPanel.Parse(msgs);
        Check(tasks.Count == 2 && tasks[0].Title == "Tarefa A" && tasks[0].EtaMin == 3
            && tasks[0].PromptSummary == "crie a classe X com propósito Y"
            && tasks[0].ToolCalls.Count == 2 && tasks[0].ToolCalls[0] == "shell"
            && tasks[1].PromptSummary == "compile o projeto" && tasks[1].ToolCalls.Count == 0,
            "U-task-summary-tools");
    }

    private static void LegacyFormat()
    {
        // Mensagens antigas (sem "— enunciado:") seguem parseando título + ETA.
        var msgs = new[]
        {
            ("assistant", "[Orquestrador] Dividi em 1 tarefa(s):\n1. Tarefa Antiga (~5 min)"),
        };
        var tasks = SessionTaskPanel.Parse(msgs);
        Check(tasks.Count == 1 && tasks[0].Title == "Tarefa Antiga" && tasks[0].EtaMin == 5
            && tasks[0].PromptSummary == "", "U-task-legacy");
        Check(SessionTaskPanel.CleanTitle("Tarefa X (~2 min) — enunciado:bla") == "Tarefa X", "U-task-cleantitle");
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
