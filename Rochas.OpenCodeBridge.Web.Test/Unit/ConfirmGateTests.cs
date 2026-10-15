// Testes unitários do portão de confirmação (aval por tool, fixo na sessão).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only

using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class ConfirmGateTests
{
    private static int Failures;

    internal static int Run()
    {
        NeedsGate();
        NeedsGateCase();
        ApprovedYes();
        ApprovedFileTrap();
        ApprovedNo();
        StickyPerTool();
        StickyShellSlot();
        Labels();
        PendingAnswer();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void NeedsGate()
    {
        Check(ConfirmGate.NeedsConfirmation("write", "{\"path\":\"a.txt\"}"), "U-gate-write");
        Check(ConfirmGate.NeedsConfirmation("edit", "{\"path\":\"a.txt\"}"), "U-gate-edit");
        Check(!ConfirmGate.NeedsConfirmation("read", "{\"path\":\"a.txt\"}"), "U-gate-read-free");
        Check(!ConfirmGate.NeedsConfirmation("shell", "{\"command\":\"ls -la\"}"), "U-gate-shell-ls-free");
        Check(ConfirmGate.NeedsConfirmation("shell", "{\"command\":\"git push origin main\"}"), "U-gate-shell-push");
        Check(ConfirmGate.NeedsConfirmation("bash", "{\"command\":\"git push\"}"), "U-gate-bash-push");
        Check(!ConfirmGate.NeedsConfirmation("shell", "{\"command\":\"git commit -m \\\"push it\\\"\"}"), "U-gate-commit-msg-free");
    }

    private static void NeedsGateCase()
    {
        Check(ConfirmGate.NeedsConfirmation("Write", "{\"path\":\"a.txt\"}"), "U-gate-write-case");
        Check(ConfirmGate.NeedsConfirmation(" WRITE ", "{\"path\":\"a.txt\"}"), "U-gate-write-trim");
        Check(ConfirmGate.NeedsConfirmation("Shell", "{\"command\":\"git push\"}"), "U-gate-shell-case");
    }

    private static void ApprovedYes()
    {
        Check(ConfirmGate.ApprovedBy("pode fazer"), "U-gate-ok-curto");
        Check(ConfirmGate.ApprovedBy("Pode fazer, escreva o arquivo."), "U-gate-ok-frase");
        Check(ConfirmGate.ApprovedBy("aprovado!"), "U-gate-ok-aprovado");
        Check(ConfirmGate.ApprovedBy("autorizo o push"), "U-gate-ok-autorizo");
        Check(ConfirmGate.ApprovedBy("vai em frente"), "U-gate-ok-idiom");
        Check(ConfirmGate.ApprovedBy("O plano está aprovado, pode continuar"), "U-gate-ok-meio");
    }

    // Armadilha da sessão 19: nome de arquivo com "aprovado" não é aval.
    private static void ApprovedFileTrap()
    {
        Check(!ConfirmGate.ApprovedBy("write /tmp/aprovado.txt"), "U-gate-trap-path");
        Check(!ConfirmGate.ApprovedBy("crie o arquivo aprovado.txt"), "U-gate-trap-bare");
        Check(!ConfirmGate.ApprovedBy("escreva em /tmp/aprovado.txt o texto oi"), "U-gate-trap-frase");
        Check(!ConfirmGate.ApprovedBy("crie aprovado.txt com o texto pode"), "U-gate-trap-quase");
    }

    private static void ApprovedNo()
    {
        Check(!ConfirmGate.ApprovedBy(null), "U-gate-no-null");
        Check(!ConfirmGate.ApprovedBy("   "), "U-gate-no-blank");
        Check(!ConfirmGate.ApprovedBy("ok"), "U-gate-no-ok");
        Check(!ConfirmGate.ApprovedBy("sim"), "U-gate-no-sim");
        Check(!ConfirmGate.ApprovedBy("escreva o arquivo, por favor"), "U-gate-no-pedido");
    }

    // Aval fixo por tool na sessão: aprovou write, write segue livre; edit não.
    private static void StickyPerTool()
    {
        int sid = 910101;
        ConfirmGate.ClearSession(sid);
        Check(!ConfirmGate.IsAllowed(sid, "write"), "U-gate-sticky-antes");
        ConfirmGate.Allow(sid, "write");
        Check(ConfirmGate.IsAllowed(sid, "write"), "U-gate-sticky-depois");
        Check(!ConfirmGate.IsAllowed(sid, "edit"), "U-gate-sticky-outra-tool");
        Check(!ConfirmGate.IsAllowed(sid + 1, "write"), "U-gate-sticky-outra-sessao");
        ConfirmGate.ClearSession(sid);
        Check(!ConfirmGate.IsAllowed(sid, "write"), "U-gate-sticky-limpo");
    }

    // Família shell (shell/bash/sh/exec/proc) conta como um slot só.
    private static void StickyShellSlot()
    {
        int sid = 910102;
        ConfirmGate.ClearSession(sid);
        ConfirmGate.Allow(sid, "bash");
        Check(ConfirmGate.IsAllowed(sid, "shell"), "U-gate-slot-shell");
        Check(ConfirmGate.IsAllowed(sid, "sh"), "U-gate-slot-sh");
        Check(!ConfirmGate.IsAllowed(sid, "write"), "U-gate-slot-write-fora");
        Check(ConfirmGate.ToolSlot("exec") == "shell", "U-gate-slot-exec");
        ConfirmGate.ClearSession(sid);
    }

    // Rótulo amigável da categoria p/ a pergunta ao usuário.
    private static void Labels()
    {
        Check(ConfirmGate.Label("write", "{}") == "escrever em arquivos", "U-gate-label-write");
        Check(ConfirmGate.Label("edit", "{}") == "editar arquivos", "U-gate-label-edit");
        Check(ConfirmGate.Label("shell", "{\"command\":\"git push\"}") == "enviar código (git push)", "U-gate-label-push");
        Check(ConfirmGate.Label("shell", "{\"command\":\"ls\"}") == "executar comando", "U-gate-label-shell");
        Check(ConfirmGate.AskFor("write", "{}") == "O agente está tentando escrever em arquivos, você permite?", "U-gate-ask");
    }

    private static void PendingAnswer()
    {
        Check(!ConfirmGate.Answer(910103, 1, true), "U-gate-answer-sem-pending");
        int a = ConfirmGate.NextId();
        int b = ConfirmGate.NextId();
        Check(b > a, "U-gate-nextid");
    }
}
