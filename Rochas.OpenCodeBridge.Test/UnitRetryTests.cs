using System.Net.Http;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Units das regras novas de 2026-10-30: cauda de tools composta pelo schema,
// permissões do role no system, temperatura de desvio no retry e nudge do
// aborto do upstream. São unidades puras: não tocam rede nem GPU.
internal static class UnitRetryTests
{
    public static Task TailsOnlyPresentTools()
    {
        string onlyWrite = global::Rochas.OpenCodeBridge.Core.HarnessTails.ComposeTools(new[] { "write" });
        TestContext.Check(onlyWrite.Contains("contrato de cada tool vai no schema"), "cauda aponta o schema como fonte");
        TestContext.Check(onlyWrite.Contains("write para arquivo"), "cauda cita a tool presente");
        TestContext.Check(!onlyWrite.Contains("shell para executar"),
            "cauda NAO anuncia shell quando o schema so tem write");
        TestContext.Check(!onlyWrite.Contains("AO CODIFICAR"),
            "ordem write->shell so aparece quando as duas tools existem");
        TestContext.Check(!onlyWrite.Contains("webfetch"), "cauda nao anuncia rede ausente");
        TestContext.Check(!onlyWrite.Contains("`edit`"), "cauda write-only nao cita edit nem em par");

        string both = global::Rochas.OpenCodeBridge.Core.HarnessTails
            .ComposeTools(new[] { "write", "shell", "read" });
        TestContext.Check(both.Contains("AO CODIFICAR"),
            "com write+shell a ordem de validacao aparece");
        TestContext.Check(both.Contains("`dotnet build`"),
            "shell aparece com os comandos concretos (build/teste)");

        string full = global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools;
        TestContext.Check(full.Contains("shell para executar comandos"),
            "default (sem filtro) descreve os comandos da shell");
        TestContext.Check(full.Contains("--project"), "default mantem a regra do dotnet");
        TestContext.Check(full.Contains("bloqueio de role"), "default mantem bloqueio de role");
        return Task.CompletedTask;
    }

    public static Task RetryTemperatureUsesDetourRole()
    {
        var profiles = global::Rochas.OpenCodeBridge.Core.RoleProfiles.DefaultCopy();
        double plan = profiles["plan"].Temperature;
        TestContext.Check(
            Math.Abs(global::Rochas.OpenCodeBridge.Core.Sampling.ForRetry(0.1, profiles) - plan) < 0.0001,
            "retry sobe da temperatura de build para a do role plan");
        TestContext.Check(
            Math.Abs(global::Rochas.OpenCodeBridge.Core.Sampling.ForRetry(0.9, profiles) - 0.9) < 0.0001,
            "retry nao esfria um turno que ja estava mais quente");
        var custom = global::Rochas.OpenCodeBridge.Core.RoleProfiles.DefaultCopy();
        custom["plan"] = new global::Rochas.OpenCodeBridge.Core.RoleProfile(0.45, "events");
        TestContext.Check(
            Math.Abs(global::Rochas.OpenCodeBridge.Core.Sampling.ForRetry(0.1, custom) - 0.45) < 0.0001,
            "valor do desvio vem do perfil, nao de literal no codigo");
        return Task.CompletedTask;
    }

    public static Task AbortNudgeRewritesTurn()
    {
        var body = new JsonObject
        {
            ["model"] = "m",
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "S" },
                new JsonObject { ["role"] = "user", ["content"] = "faca" })
        };
        string? retried = global::Rochas.OpenCodeBridge.Core.RetryNudge
            .WithAbortNudge(body.ToJsonString(), 0.1);
        TestContext.Check(retried is not null, "corpo com historico e' reescrito");
        var node = JsonNode.Parse(retried!)!.AsObject();
        var msgs = node["messages"]!.AsArray();
        TestContext.Check(msgs.Count == 3, "nudge entra como mensagem de usuario");
        string nudge = msgs[^1]!["content"]!.GetValue<string>();
        TestContext.Check(nudge.Contains("OUTRA abordagem"), "nudge pede outra abordagem");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.BridgeNotice.Is(nudge),
            "nudge usa o prefixo unico da bridge");
        TestContext.Check(node["temperature"]!.GetValue<double>() > 0.1,
            "retry sobe a temperatura do turno");

        var noHistory = new JsonObject { ["model"] = "m" };
        TestContext.Check(
            global::Rochas.OpenCodeBridge.Core.RetryNudge.WithAbortNudge(noHistory.ToJsonString(), 0.1) is null,
            "corpo sem historico nao ganha retry as cegas");
        return Task.CompletedTask;
    }

    public static Task DetectsAbortAndRetryHint()
    {
        var ex = new HttpRequestException("The response ended prematurely. (ResponseEnded)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.RetryNudge.IsUpstreamAbort(ex),
            "ResponseEnded e' aborto do upstream");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.RetryNudge.IsUpstreamAbort(
                new HttpRequestException("Connection refused")),
            "recusa de conexao nao e' aborto de geracao");

        var withHint = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "pedido" },
            new JsonObject { ["role"] = "user", ["content"] = global::Rochas.OpenCodeBridge.Core.RetryNudge.Notice() });
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.RetryNudge.HasRetryHint(withHint),
            "nudge de retry no historico e' detectado");

        var breaker = new JsonArray(
            new JsonObject { ["role"] = "tool", ["content"] = "ERRO: falhou" + global::Rochas.OpenCodeBridge.Core.ToolsAntiRepeat.NudgeNotice.Replace("{N}", "3") });
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.RetryNudge.HasRetryHint(breaker),
            "nudge do breaker tambem conta como turno de retry");

        // Desvio e' do TURNO do retry: nudge que ja nao e' a ultima mensagem
        // (o modelo ja respondeu) nao pode continuar esquentando o pedido.
        var stale = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = global::Rochas.OpenCodeBridge.Core.RetryNudge.Notice() },
            new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
            new JsonObject { ["role"] = "tool", ["content"] = "gravado" },
            new JsonObject { ["role"] = "user", ["content"] = "proximo arquivo" });
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.RetryNudge.HasRetryHint(stale),
            "temperatura de desvio NAO persiste nos turnos seguintes");

        var clean = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "pedido" });
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.RetryNudge.HasRetryHint(clean),
            "pedido normal nao sobe temperatura");
        return Task.CompletedTask;
    }

    public static Task RolePermissionsInSystem()
    {
        string plan = global::Rochas.OpenCodeBridge.Core.MainHarness
            .PermissionsLine("Plan", new[] { "write", "shell" });
        TestContext.Check(plan.Contains("NAO pode chamar"), "role com bloqueio enuncia a proibicao");
        TestContext.Check(plan.Contains("escrita") && plan.Contains("execucao"),
            "proibicao descrita como ACAO (escrita/execucao), nao so nome de tool");
        TestContext.Check(plan.Contains("nao contorne por shell"), "mantem a regra de nao contornar");

        string build = global::Rochas.OpenCodeBridge.Core.MainHarness.PermissionsLine("Build", Array.Empty<string>());
        TestContext.Check(build.Contains("nenhuma tool bloqueada"), "role sem blacklist e' declarado livre");

        string generic = global::Rochas.OpenCodeBridge.Core.MainHarness.PermissionsLine("", null);
        TestContext.Check(generic.Length == 0, "cliente sem role nao recebe bloco de permissao");

        string system = global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem(null, "Plan", new[] { "write" });
        TestContext.Check(system.Contains("PERMISSOES DO ROLE (Plan)"), "system do role carrega a permissao");
        string main = global::Rochas.OpenCodeBridge.Core.MainHarness.Text;
        TestContext.Check(!main.Contains("Prefira a tool dedicada"),
            "main harness nao duplica a cauda de tools (fonte unica)");
        return Task.CompletedTask;
    }
}
