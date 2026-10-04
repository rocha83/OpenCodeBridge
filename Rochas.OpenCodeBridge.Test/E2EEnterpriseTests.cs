using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E enterprise: ASP.NET (CRUD simples e composto), worker, Garnet e React+dashboard.</summary>
internal static class E2EEnterpriseTests
{
    static JsonObject Req(string text, bool stream = false) => new()
    {
        ["model"] = "openai/qwen3-8b-awq-build",
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

    static void CheckNoStub(string text, string ctx)
    {
        TestContext.Check(!text.Contains("TODO", StringComparison.OrdinalIgnoreCase), ctx + " sem TODO");
        TestContext.Check(!text.Contains("NotImplementedException"), ctx + " sem NotImplemented");
        TestContext.Check(!text.Contains("/* ... */") && !text.Contains("// ..."), ctx + " sem esqueleto");
    }

    public static async Task CrudProduct()
    {
        // CRUD simples: Product (Minimal API + Dapper, validacao de borda).
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (Id, Name, Price, Stock). " +
            "Entregue: DDL da tabela, GET /api/products (lista), GET /api/products/{id} (404 se ausente), " +
            "POST (400 se Name vazio ou Price <= 0), PUT (404 se ausente) e DELETE (204). " +
            "Use Results<T> tipados e uma linha de log por operacao. So codigo + 3 bullets de borda."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "product completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "product com codigo util");
        CheckNoStub(text, "product");
        TestContext.Check(text.Contains("MapPost") && text.Contains("MapDelete"), "product com endpoints");
        Console.WriteLine($"  [info] product dapper={text.Contains("Dapper")} validacao={text.Contains("400")}");
    }

    public static async Task CrudCustomer()
    {
        // CRUD simples: Customer (paginacao + filtro + unicidade de email).
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 Minimal API + Dapper: CRUD de Customer (Id, Name, Email, CreatedAt). " +
            "Entregue: GET /api/customers com paginacao (?page, ?pageSize, max 100) e filtro ?q por nome/email, " +
            "POST rejeitando email duplicado (409) e email invalido (400), PUT e DELETE com 404. " +
            "Mostre o SQL com OFFSET/FETCH e o indice do email. So codigo + 3 bullets de borda."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "customer completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "customer com codigo util");
        CheckNoStub(text, "customer");
        TestContext.Check(text.Contains("409") && text.Contains("OFFSET", StringComparison.OrdinalIgnoreCase), "customer com conflito+paginacao");
        Console.WriteLine($"  [info] customer paginacao={text.Contains("pageSize")} email={text.Contains("Email")}");
    }

    public static async Task CrudSaleInvoice()
    {
        // CRUD composto header+detail: SaleInvoice com itens em transacao.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 + Dapper: POST /api/saleinvoices cria SaleInvoice (header: CustomerId, Date) + Items " +
            "(detail: ProductId, Qty, UnitPrice) em UMA transacao: total = soma(Qty*UnitPrice), valida estoque " +
            "suficiente de cada produto (409 se faltar, com rollback total) e da baixa no estoque no mesmo commit. " +
            "GET /api/saleinvoices/{id} retorna header + items. Mostre: DDL das 2 tabelas, codigo com IDbTransaction " +
            "e o que acontece se o 3o item estourar o estoque. So codigo + 3 bullets."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "saleinvoice completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 600, "saleinvoice com codigo util");
        CheckNoStub(text, "saleinvoice");
        TestContext.Check(text.Contains("BeginTransaction", StringComparison.OrdinalIgnoreCase) || text.Contains("Transaction", StringComparison.OrdinalIgnoreCase), "saleinvoice transacional");
        TestContext.Check(text.Contains("Items", StringComparison.OrdinalIgnoreCase), "saleinvoice com detalhe");
        Console.WriteLine($"  [info] saleinvoice rollback={text.Contains("Rollback", StringComparison.OrdinalIgnoreCase)} total={text.Contains("Total", StringComparison.OrdinalIgnoreCase)}");
    }

    public static async Task BackgroundWorker()
    {
        // Worker assincrono: IHostedService drenando outbox com retry e stop gracioso.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 BackgroundService: worker que drena a tabela outbox em lote de 50 a cada 5s, publica no " +
            "barramento com retry exponencial + jitter (max 5 tentativas, depois dead-letter), respeita " +
            "CancellationToken em todo await (stop gracioso sem perder a mensagem da vez) e registra throughput " +
            "no log a cada minuto. Entregue o worker completo + DDL da dead-letter. So codigo + 3 bullets."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "worker completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "worker com codigo util");
        CheckNoStub(text, "worker");
        TestContext.Check(text.Contains("BackgroundService") && text.Contains("CancellationToken"), "worker com ciclo de vida");
        Console.WriteLine($"  [info] worker retry={text.Contains("retry", StringComparison.OrdinalIgnoreCase)} deadletter={text.Contains("dead", StringComparison.OrdinalIgnoreCase)}");
    }

    public static async Task GarnetCircuit()
    {
        // Circuito assincrono com Garnet: cache-aside + invalidacao + antifalha.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 + Garnet (Redis-protocol): repositorio de Product com cache-aside — GET tenta Garnet " +
            "primeiro (IDistributedCache, sliding 5min), fallback Postgres + repopula; PUT/DELETE invalidam a " +
            "chave; stampede controlado por lock por chave; quando o Garnet cai, opera direto no banco e loga " +
            "degradado (circuit-breaker simples com 3 falhas). Entregue o repositorio + registro no DI. " +
            "So codigo + 3 bullets."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "garnet completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "garnet com codigo util");
        CheckNoStub(text, "garnet");
        TestContext.Check(text.Contains("IDistributedCache") && text.Contains("RemoveAsync"), "garnet com invalidacao");
        Console.WriteLine($"  [info] garnet sliding={text.Contains("Sliding", StringComparison.OrdinalIgnoreCase)} circuito={text.Contains("circuit", StringComparison.OrdinalIgnoreCase)}");
    }

    public static async Task ReactDashboard()
    {
        // React + dashboard consumindo o ASP.NET: cards, grafico e estados.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices?days=30 do ASP.NET e exibe: " +
            "3 cards (faturamento, ticket medio, itens vendidos), grafico de barras por dia (SVG proprio, sem lib), " +
            "tabela das 10 maiores notas com link p/ detalhe, skeleton durante load, erro com retry por widget " +
            "independente e AbortController no filtro de periodo. So codigo .tsx + 3 bullets."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "dashboard completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "dashboard com codigo util");
        CheckNoStub(text, "dashboard");
        TestContext.Check(text.Contains("AbortController") && text.Contains("useEffect"), "dashboard com ciclo de vida");
        Console.WriteLine($"  [info] dashboard cards={text.Contains("card", StringComparison.OrdinalIgnoreCase)} skeleton={text.Contains("skeleton", StringComparison.OrdinalIgnoreCase)}");
    }
}
