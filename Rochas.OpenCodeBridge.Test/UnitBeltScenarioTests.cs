namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Pin do contrato do Scripter (belt Orch -> Scripter -> Exec).
/// </summary>
/// <remarks>
/// O que este pin protege foi medido em <c>e-belts-marrom-orch</c> de 2026-10-31:
/// o system dizia "salve em X.sh", mas a tarefa do Orch entrava crua no turno do
/// usuario citando <c>src/Domain/Reimbursement.cs</c> e <c>db/Schema.sql</c> — o
/// modelo gravou os dois arquivos da tarefa 6 vezes em 8 turnos e nunca o
/// <c>.sh</c> (0 scripts validos, 49s). A belt azul, com o mesmo codigo e uma
/// tarefa sem paths de artefato, gravou <c>.cs</c> + <c>.sh</c> + <c>.md</c> e
/// passou: a diferenca era o enunciado, nao a capacidade do modelo.
/// </remarks>
internal static class UnitBeltScenarioTests
{
    public static Task ScripterPromptSeparatesDeliverableFromTask()
    {
        string sys = E2EBeltsTests.ScripterSystem("tarefa_x", "FATOS DO AMBIENTE:\n- workspace vazio\n");
        TestContext.Check(sys.Contains("tarefa_x.sh") && sys.Contains("tarefa_x.md"),
            "system do Scripter nomeia o entregavel (.sh e .md)");
        TestContext.Check(sys.Contains("NAO executa a tarefa"),
            "system diz que o Scripter ESCREVE o script, nao executa a tarefa");
        TestContext.Check(!sys.Contains("Program.fs") && !sys.Contains("printfn"),
            "system do Scripter sem literal F# (trilha arquivada)");
        string task = E2EBeltsTests.ScripterTask("Criar `src/Domain/Reimbursement.cs`");
        TestContext.Check(task.Contains("TAREFA A EXECUTAR") && task.Contains("src/Domain/Reimbursement.cs"),
            "tarefa do Orch entra como conteudo, separada do entregavel");
        return Task.CompletedTask;
    }
}
