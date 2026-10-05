using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E senior: React corporativo, bus de eventos no backend, regras de ERP e ARM embarcado.</summary>
internal static class E2ESeniorTests
{
    static JsonObject Req(string text, bool stream = false) => new()
    {
        ["model"] = "openai/qwen3-8b-awq",
        ["stream"] = stream,
        ["input"] = new JsonArray(new JsonObject
        {
            ["type"] = "message", ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text })
        })
    };

    static string TextOf(JsonObject r)
    {
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        var msg = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message")?.AsObject();
        return msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
    }

    public static async Task ReactEnterprise()
    {
        // Frontend senior: fetch concorrente com cancelamento + memoizacao.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "React 19 + TS: componente que busca painel e detalhe em paralelo de /api/panel e /api/detail/:id. " +
            "Exija: AbortController cancelando o detalhe quando o id muda, sem setState apos unmount, " +
            "useMemo/useCallback onde evita re-render real, e tratamento de erro por fonte independente. " +
            "Responda so com o codigo .tsx e 3 bullets de por que cada decisao evita bug em producao."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "react completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 400, "react com codigo util");
        Console.WriteLine($"  [info] react abort={text.Contains("AbortController")} memo={text.Contains("useMemo")}");
    }

    public static async Task EventBusBackend()
    {
        // Backend corporativo: outbox + consumidor idempotente no barramento.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 + Dapper: endpoint POST /api/schedule publica Clinic.Schedule no barramento via outbox transacional " +
            "(mesma transacao do INSERT). Desenhe: tabela outbox (colunas), publisher em background com retry exponencial + jitter, " +
            "consumidor idempotente por message-id (tabela processed_messages), e o que acontece quando o broker cai por 10 minutos. " +
            "Responda em bullets curtos + DDL da outbox."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "bus completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 400, "bus com conteudo util");
        Console.WriteLine($"  [info] bus idempotente={text.Contains("idempotente", StringComparison.OrdinalIgnoreCase)} outbox={text.Contains("outbox", StringComparison.OrdinalIgnoreCase)}");
    }

    public static async Task ErpRules()
    {
        // ERP: regra fiscal de precificacao com casos de borda.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "ERP: regra de preco final = base - desconto + imposto, onde desconto e min(desconto_pedido, teto_categoria) e " +
            "imposto incide sobre (base - desconto) com aliquota por UF, exceto itens isentos (NCM inicia com 8471). " +
            "Dê: funcao pura em C# ou Python + tabela com 4 casos de borda (desconto acima do teto, UF sem aliquota, item isento, base zero) " +
            "e o valor esperado de cada um."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "erp completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 300, "erp com conteudo util");
        Console.WriteLine($"  [info] erp borda={text.Contains("8471")}");
    }

    public static async Task ArmIot()
    {
        // Embarcado ARM: Cortex-M, UART por DMA, baixo consumo.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "ARM Cortex-M4 bare-metal em C: le sensor via ADC a cada 100ms (SysTick), envia pacote de 16 bytes por UART com DMA " +
            "em 115200 8N1, dorme em STOP entre amostras (wake por RTC), e nunca bloqueia em ISR. Entregue: main.c essencial " +
            "(init, loop, ISR curta) + 3 bullets do que quebraria sem DMA e sem STOP."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "arm completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 400, "arm com conteudo util");
        Console.WriteLine($"  [info] arm dma={text.Contains("DMA")} stop={text.Contains("STOP")}");
    }
}
