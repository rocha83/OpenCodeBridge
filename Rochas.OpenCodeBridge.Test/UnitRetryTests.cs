using System.Net.Http;
using System.Text.Json.Nodes;
using Tails = Rochas.OpenCodeBridge.Core.HarnessTails;
using Smp = Rochas.OpenCodeBridge.Core.Sampling;

namespace Rochas.OpenCodeBridge.Test;

// Units das regras novas de 2026-10-30: cauda de tools composta pelo schema,
// permissões do role no system, temperatura de desvio no retry e nudge do
// aborto do upstream. São unidades puras: não tocam rede nem GPU.
internal static class UnitRetryTests
{
    /// <summary>
    /// O portao de fluxo (PENDINGS P1) exige "cauda de tools presente" sempre que
    /// o request declara `tools`. A cauda e' composta pelo schema, entao o texto
    /// final nao e' igual ao default inteiro: o reconhecimento tem que ser pelo
    /// cabecalho (sentinela), nunca pela igualdade do texto.
    /// </summary>
    public static Task ToolsTailMarker()
    {
        // 1. Cauda completa: reconhecida.
        string full = Tails.Compose("system", true, Tails.DefaultBehavior, Tails.DefaultTools);
        TestContext.Check(Tails.HasToolsTail(full, Tails.DefaultTools),
            "cauda completa e' reconhecida no system final");

        // 2. Cauda composta pelo schema (subconjunto): o texto NAO e' o default,
        //    mas a cauda esta' la' — era o falso negativo medido em 2026-10-30.
        string narrowed = Tails.Compose("system", true, Tails.DefaultBehavior, Tails.DefaultTools, true,
            new[] { "read", "write", "shell" });
        TestContext.Check(!narrowed.Contains(Tails.DefaultTools, StringComparison.Ordinal),
            "cauda filtrada nao contem o texto default inteiro (por isso a igualdade falhava)");
        TestContext.Check(Tails.HasToolsTail(narrowed, Tails.DefaultTools),
            "cauda filtrada pelo schema e' reconhecida pelo cabecalho");

        // 3. Sem tools no request a cauda sai de proposito: nao pode ser reconhecida.
        string noTools = Tails.Compose("system", true, Tails.DefaultBehavior, Tails.DefaultTools, false);
        TestContext.Check(!Tails.HasToolsTail(noTools, Tails.DefaultTools),
            "pedido sem tools nao carrega cauda de tools");

        // 4. Override do deployment: reconhecido pelo proprio prefixo.
        const string customTail = "CAUDA CUSTOMIZADA DO DEPLOYMENT: use as tools do schema.";
        string withCustom = Tails.Compose("system", true, "", customTail);
        TestContext.Check(Tails.HasToolsTail(withCustom, customTail),
            "override de cauda e' reconhecido pelo prefixo");
        TestContext.Check(!Tails.HasToolsTail(withCustom, Tails.DefaultTools),
            "override nao passa como cauda default");
        TestContext.Check(!Tails.HasToolsTail("", Tails.DefaultTools) && !Tails.HasToolsTail(full, ""),
            "system ou cauda vazios nao atestam nada");
        return Task.CompletedTask;
    }

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

        // Valor global do host para o desvio (--aux-temp): MESMA precedencia da
        // temperatura do turno — role resolvido manda; sem role, vale o global.
        TestContext.Check(
            Math.Abs(Smp.ForRetry(0.1, Smp.DetourReference(true, profiles, 0.7)) - plan) < 0.0001,
            "com role resolvido o desvio continua sendo o do role, nao o valor do host");
        TestContext.Check(
            Math.Abs(Smp.ForRetry(0.1, Smp.DetourReference(false, profiles, 0.7)) - 0.7) < 0.0001,
            "sem role resolvido o valor do host preenche o desvio");
        TestContext.Check(
            Math.Abs(Smp.ForRetry(0.1, Smp.DetourReference(false, profiles, double.NaN)) - plan) < 0.0001,
            "sem role e sem valor do host o desvio volta ao perfil de desvio");
        TestContext.Check(
            Math.Abs(Smp.ForRetry(0.9, Smp.DetourReference(false, profiles, 0.3)) - 0.9) < 0.0001,
            "o desvio nunca esfria um turno que ja estava mais quente");
        TestContext.Check(
            Math.Abs(Smp.ForRetry(0.2, double.NaN) - 0.2) < 0.0001,
            "referencia ausente (NaN) mantem a temperatura do turno");
        TestContext.Check(
            Math.Abs(Smp.DetourReference(false, profiles, double.NaN) - plan) < 0.0001,
            "sem valor do host a referencia de desvio e' o perfil (tabela unica)");

        var auxBody = new JsonObject
        {
            ["model"] = "m",
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "S" },
                new JsonObject { ["role"] = "user", ["content"] = "faca" })
        };
        double auxRef = Smp.DetourReference(false, profiles, 0.7);
        string? auxRetried = global::Rochas.OpenCodeBridge.Core.RetryNudge
            .WithAbortNudge(auxBody.ToJsonString(), 0.1, auxRef);
        TestContext.Check(auxRetried is not null
            && Math.Abs(JsonNode.Parse(auxRetried!)!["temperature"]!.GetValue<double>() - 0.7) < 0.0001,
            "nudge do aborto leva a referencia de desvio para o corpo do retry");
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

    /// <summary>
    /// Loop agentico nao acumula system: harness/cauda entram UMA vez por
    /// request, mesmo quando o cliente devolve o system composto do turno
    /// anterior (e system/developer espalhados colapsam num so').
    /// </summary>
    public static Task PromptDedupInAgenticLoop()
    {
        // 1. BuildSystem idempotente: reaplicar sobre o proprio resultado nao cresce.
        string once = global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem("Voce e o agente build.", "Build", null);
        string twice = global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem(once, "Build", null);
        TestContext.Check(once == twice, "BuildSystem idempotente: reaplicar nao cresce o system");
        TestContext.Check(
            once.IndexOf(global::Rochas.OpenCodeBridge.Core.SystemPromptDedup.HarnessMarker, StringComparison.Ordinal)
            == once.LastIndexOf(global::Rochas.OpenCodeBridge.Core.SystemPromptDedup.HarnessMarker, StringComparison.Ordinal),
            "harness aparece uma unica vez");
        TestContext.Check(once.Contains("Voce e o agente build."),
            "system do cliente preservado no system composto");

        // 2. Apply em dois turnos do mesmo historico.
        var msgs = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "faz o pedido" });
        global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(msgs, true, "Build", null);
        global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(msgs, true, "Build", null);
        TestContext.Check(msgs.Count == 2, "Apply nao acumula mensagem de system a cada turno");
        string sysTwo = msgs[0]!["content"]!.GetValue<string>()!;
        TestContext.Check(msgs[0]!["role"]!.GetValue<string>() == "system", "system continua no topo");
        TestContext.Check(
            sysTwo.IndexOf(global::Rochas.OpenCodeBridge.Core.SystemPromptDedup.HarnessMarker, StringComparison.Ordinal)
            == sysTwo.LastIndexOf(global::Rochas.OpenCodeBridge.Core.SystemPromptDedup.HarnessMarker, StringComparison.Ordinal),
            "harness unico depois de dois turnos");
        TestContext.Check(msgs[1]!["role"]!.GetValue<string>() == "user", "historico do cliente intacto");

        // 3. system + developer espalhados colapsam numa mensagem inicial.
        var multi = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "system A" },
            new JsonObject { ["role"] = "developer", ["content"] = "developer B" },
            new JsonObject { ["role"] = "user", ["content"] = "pedido" });
        global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(multi, true, "Build", null);
        TestContext.Check(multi.Count == 2, "system e developer colapsam numa so' mensagem");
        string sysMulti = multi[0]!["content"]!.GetValue<string>()!;
        TestContext.Check(sysMulti.Contains("system A") && sysMulti.Contains("developer B"),
            "os dois textos de system foram preservados");
        TestContext.Check(multi[1]!["content"]!.GetValue<string>() == "pedido",
            "mensagem do usuario segue intacta");

        // 4. Compose idempotente: cauda (data/behavior/tools) nao duplica.
        string c1 = global::Rochas.OpenCodeBridge.Core.HarnessTails.Compose("system do cliente", true,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultBehavior,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools);
        string c2 = global::Rochas.OpenCodeBridge.Core.HarnessTails.Compose(c1, true,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultBehavior,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools);
        TestContext.Check(c1 == c2, "Compose idempotente: cauda nao duplica no turno seguinte");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.SystemPromptDedup.StripTails(c1) == "system do cliente",
            "StripTails devolve exatamente o system do cliente");

        // 5. Clean corta os dois lados (harness na frente, cauda atras).
        string full = global::Rochas.OpenCodeBridge.Core.HarnessTails.Compose(
            global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem("system do cliente", "Build", null),
            true,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultBehavior,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools);
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.SystemPromptDedup.Clean(full).Trim() == "system do cliente",
            "Clean devolve so' o system original do cliente");
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
