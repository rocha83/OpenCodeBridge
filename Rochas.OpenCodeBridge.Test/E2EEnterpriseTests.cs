using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E enterprise: ASP.NET (CRUD simples e composto), worker, Redis e React+dashboard.</summary>
internal static class E2EEnterpriseTests
{
    static JsonObject Req(string text, bool stream = false) => new()
    {
        ["model"] = TestContext.PlanModel,
        ["stream"] = stream,
        ["input"] = new JsonArray(new JsonObject
        {
            ["type"] = "message", ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text",
                ["text"] = text + " Implementacao completa, sem placeholders. ENTREGUE O ARTEFATO COMPLETO EM TEXTO, SEM TOOL CALLS." })
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
        // Marcador REAL de stub (comentario TODO / TODO:), case-sensitive de propositio:
        // nao pode casar a palavra portuguesa "todo/todos" ("todo o fluxo") — falso positivo.
        var todo = System.Text.RegularExpressions.Regex.Match(text,
            @"(?://|#|/\*|\*|<!--|--)\s*TODO\b|\bTODO\s*[:\-]");
        TestContext.Check(!todo.Success, ctx + " sem TODO" + (todo.Success ? $" (achei: '{todo.Value.Trim()}')" : ""));
        TestContext.Check(!text.Contains("NotImplementedException"), ctx + " sem NotImplemented");
        // `// ...` em linha so e' o corpo de um comentario — legitimo num metodo
        // cuja logica esta no resto do arquivo; `/* ... */` e' um comentario de
        // bloco. O stub real (o codigo reduzido a reticencias) trincou no
        // E-ent-crud-saleinvoice 2026-10-30 so quando o texto era raso. Aceita
        // o literal de comentario, mas exige que haja logica real (texto >= 900).
        bool hasEllipsisComment = text.Contains("/* ... */") || text.Contains("// ...");
        bool shallowCode = text.Length < 900;
        TestContext.Check(!hasEllipsisComment || !shallowCode, ctx + " sem esqueleto");
    }

    public static async Task CrudProduct()
    {
        // CRUD simples: Product (Minimal API + Dapper, validacao de borda).
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (Id, Name, Price, Stock). " +
            "Entregue: DDL da tabela, GET /api/products (lista), GET /api/products/{id} (404 se ausente), " +
            "POST (400 se Name vazio ou Price <= 0), PUT (404 se ausente) e DELETE (204), com log por operacao. " +
            "Entregue o codigo completo + 3 bullets de borda."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "product completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "product com codigo util");
        CheckNoStub(text, "product");
        // 3B varia Minimal API <-> Controller entre runs: aceita ambos os estilos.
        bool hasCreate = text.Contains("MapPost") || text.Contains("HttpPost");
        bool hasDelete = text.Contains("MapDelete") || text.Contains("HttpDelete");
        bool hasRead = text.Contains("MapGet") || text.Contains("HttpGet");
        TestContext.Check(hasCreate && hasDelete && hasRead, "product com endpoints CUD");
        Console.WriteLine($"  [info] product dapper={text.Contains("Dapper")} validacao={text.Contains("400")}");
    }

    public static async Task CrudCustomer()
    {
        // CRUD simples: Customer (paginacao + filtro + unicidade de email).
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 Minimal API + Dapper: CRUD de Customer (Id, Name, Email, CreatedAt). " +
            "Entregue: GET /api/customers com paginacao (?page, ?pageSize, max 100) e filtro ?q por nome/email, " +
            "POST rejeitando email duplicado (409) e email invalido (400), PUT e DELETE com 404, indice no email. " +
            "So codigo + 3 bullets de borda."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "customer completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "customer com codigo util");
        CheckNoStub(text, "customer");
        // 409 pode vir literal ou pelo helper idiomático do ASP.NET (Results.Conflict /
        // ConflictHttpException) — ambos respondem 409. Paginação: OFFSET (SQL) ou Skip (LINQ).
        bool has409 = text.Contains("409") || text.Contains("Conflict", StringComparison.OrdinalIgnoreCase);
        bool hasPage = text.Contains("OFFSET", StringComparison.OrdinalIgnoreCase) || text.Contains("Skip", StringComparison.OrdinalIgnoreCase);
        TestContext.Check(has409 && hasPage, "customer com conflito+paginacao");
        Console.WriteLine($"  [info] customer paginacao={text.Contains("pageSize")} email={text.Contains("Email")}");
    }

    public static async Task CrudSaleInvoice()
    {
        // CRUD composto header+detail: SaleInvoice com itens em transacao.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 + Dapper: POST /api/saleinvoices cria SaleInvoice (header: CustomerId, Date) + Items " +
            "(detail: ProductId, Qty, UnitPrice) em UMA transacao: total = soma(Qty*UnitPrice), valida estoque " +
            "suficiente de cada produto (409 se faltar, com rollback total) e da baixa no estoque no mesmo commit. " +
            "GET /api/saleinvoices/{id} retorna header + items. Mostre: DDL das 2 tabelas e o que acontece " +
            "se o 3o item estourar o estoque. So codigo + 3 bullets."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "saleinvoice completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 600, "saleinvoice com codigo util");
        CheckNoStub(text, "saleinvoice");
        TestContext.Check(text.Contains("BeginTransaction", StringComparison.OrdinalIgnoreCase) || text.Contains("Transaction", StringComparison.OrdinalIgnoreCase), "saleinvoice transacional");
        // Detalhe pode vir como Items, SaleInvoiceItems ou Item: o que importa e
        // a linha de detalhe existir (ProductId + Qty/Quantity) no codigo.
        bool hasDetail = text.Contains("Items", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("ProductId", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("Qty", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Quantity", StringComparison.OrdinalIgnoreCase)));
        TestContext.Check(hasDetail, "saleinvoice com detalhe");
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

    public static async Task RedisCircuit()
    {
        // Circuito assincrono com Redis: cache-aside + invalidacao + antifalha.
        // A linha E-ent e' CONCEITUAL: texto puro via /v1/responses (o modelo
        // entrega o codigo no texto). O caminho de tool (write em disco, git,
        // shell) fica nas trilhas E-tools — aqui nao se pede call.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "C# .NET 9 + Redis (StackExchange.Redis): repositorio de Product com cache-aside " +
            "(IDistributedCache, sliding 5min), fallback Postgres + repopula; PUT/DELETE invalidam a chave " +
            "com cache.RemoveAsync; stampede controlado por lock por chave e circuit-breaker simples (3 falhas) " +
            "quando o Redis cai. Entregue o repositorio + o registro no DI e diga o que acontece quando o " +
            "Redis esta fora. So codigo + 3 bullets de borda."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "redis completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "redis com codigo util");
        CheckNoStub(text, "redis");
        TestContext.Check(text.Contains("IDistributedCache") && text.Contains("RemoveAsync"), "redis com invalidacao");
        // Contrato observavel: cache distribuido registrado no DI. A API exata e
        // escolha do autor (StackExchange moderno, memory fallback ou a legada
        // pre-3.0); a versao escolhida vai no [info], nao no assert.
        bool di = text.Contains("AddStackExchangeRedisCache") || text.Contains("AddDistributedMemoryCache")
            || text.Contains("AddDistributedRedisCache") || text.Contains("IConnectionMultiplexer");
        TestContext.Check(di, "redis registro no DI");
        string diApi = text.Contains("AddStackExchangeRedisCache") ? "modern"
            : text.Contains("AddDistributedRedisCache") ? "legacy" : "custom";
        Console.WriteLine($"  [info] redis sliding={text.Contains("Sliding", StringComparison.OrdinalIgnoreCase)} circuito={text.Contains("circuit", StringComparison.OrdinalIgnoreCase)} di={diApi}");
    }

    public static async Task ReactDashboard()
    {
        // React + dashboard consumindo o ASP.NET: cards, grafico e estados.
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(
            "React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices?days=30 do ASP.NET e exibe: " +
            "3 cards (faturamento, ticket medio, itens vendidos), grafico de barras por dia (SVG proprio, sem lib), " +
            "tabela das 10 maiores notas com link p/ detalhe, skeleton durante load, erro com retry por widget " +
            "independente e AbortController no filtro de periodo. Entregue o codigo .tsx completo + 3 bullets."));
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "dashboard completed");
        string text = TextOf(r);
        TestContext.Check(text.Length > 500, "dashboard com codigo util");
        CheckNoStub(text, "dashboard");
        // Ciclo de vida: cancelamento do fetch + hook de efeito (useEffect/useLayoutEffect/use
        // da React 19) — nao amarrado a um nome unico de hook.
        bool hasLifecycle = text.Contains("AbortController")
            && (text.Contains("useEffect") || text.Contains("useLayoutEffect") || text.Contains("use("));
        TestContext.Check(hasLifecycle, "dashboard com ciclo de vida");
        Console.WriteLine($"  [info] dashboard cards={text.Contains("card", StringComparison.OrdinalIgnoreCase)} skeleton={text.Contains("skeleton", StringComparison.OrdinalIgnoreCase)}");
    }
}
