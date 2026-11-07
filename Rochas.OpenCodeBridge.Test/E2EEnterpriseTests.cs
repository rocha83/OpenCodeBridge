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

    /// <summary>
    /// Motivo de stub (mesmas regras do <see cref="CheckNoStub"/>), para o retry
    /// de contrato: vazio = sem stub.
    /// </summary>
    static string StubReason(string text)
    {
        var todo = System.Text.RegularExpressions.Regex.Match(text,
            @"(?://|#|/\*|\*|<!--|--)\s*TODO\b|\bTODO\s*[:\-]");
        if (todo.Success) return "TODO";
        if (text.Contains("NotImplementedException")) return "NotImplementedException";
        if ((text.Contains("/* ... */") || text.Contains("// ...")) && text.Length < 900) return "esqueleto";
        return "";
    }

    /// <summary>
    /// Pede o artefato em TEXTO e repete UMA vez quando o contrato do cenario
    /// nao e' atendido. A linha E-ent e' conceitual e o modelo varia com
    /// temperature 0.3 (medido 2026-11-07: amostras sem o literal 409/UNIQUE,
    /// com NotImplementedException e com paginacao so LIMIT). O retry fica
    /// logado e os asserts continuam valendo sobre a ultima amostra.
    /// </summary>
    static async Task<(string Text, string Status)> Ask(string label, string prompt,
        Func<string, bool> contract, int attempts = 2)
    {
        string text = "", status = "";
        for (int n = 1; n <= attempts; n++)
        {
            var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(prompt));
            status = r["status"]?.GetValue<string>() ?? "";
            if (status != "completed")
            {
                Console.WriteLine($"  [retry] {label} {n}/{attempts}: status={status}");
                continue;
            }
            text = TextOf(r);
            if (contract(text)) return (text, status);
            Console.WriteLine($"  [retry] {label} {n}/{attempts}: contrato nao atendido (len={text.Length})");
        }
        return (text, status);
    }

    /// <summary>
    /// Quando um assert de conteudo cai, o log do step mostra o que o modelo
    /// entregou — sem isso o diagnostico vira adivinhacao de qual token faltou.
    /// </summary>
    static void FailDump(string ctx, string text)
    {
        string head = text.Length > 800 ? text[..800] : text;
        Console.WriteLine($"  [fail-dump] {ctx} ({text.Length} chars): " +
            head.Replace("\r", "").Replace("\n", " | "));
    }

    public static async Task CrudProduct()
    {
        // CRUD simples: Product (Minimal API + Dapper, validacao de borda).
        const string prompt =
            "C# .NET 9 Minimal API + Dapper + Postgres: CRUD completo de Product (Id, Name, Price, Stock). " +
            "Entregue: DDL da tabela, GET /api/products (lista), GET /api/products/{id} (404 se ausente), " +
            "POST (400 se Name vazio ou Price <= 0), PUT (404 se ausente) e DELETE (204), com log por operacao. " +
            "Entregue o codigo completo + 3 bullets de borda.";
        static bool Cud(string t) =>
            (t.Contains("MapPost") || t.Contains("HttpPost")) &&
            (t.Contains("MapDelete") || t.Contains("HttpDelete")) &&
            (t.Contains("MapGet") || t.Contains("HttpGet"));
        var (text, status) = await Ask("product", prompt,
            t => t.Length > 500 && StubReason(t) == "" && Cud(t));
        TestContext.Check(status == "completed", "product completed");
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
        // O dialeto e' FIXO (Postgres) — sem isso o modelo variava entre
        // OFFSET/FETCH (SQL Server), Skip (LINQ) e LIMIT puro, e o assert caia.
        const string prompt =
            "C# .NET 9 Minimal API + Dapper + Postgres: CRUD de Customer (Id, Name, Email, CreatedAt). " +
            "Entregue: GET /api/customers com paginacao SQL usando LIMIT ? OFFSET ? (?page, ?pageSize, max 100) " +
            "e filtro ?q por nome/email (ILIKE), POST rejeitando email duplicado pela constraint UNIQUE(email) " +
            "respondendo 409 (Results.Conflict) e email invalido respondendo 400, PUT e DELETE com 404, " +
            "indice no email. So codigo + 3 bullets de borda.";
        // Contrato (nao literal): duplicado = 409|Conflict|duplicad|UNIQUE;
        // paginacao = OFFSET|LIMIT|Skip|pageSize (o dialeto agora esta fixado).
        static bool Has409(string t) => t.Contains("409")
            || t.Contains("Conflict", StringComparison.OrdinalIgnoreCase)
            || t.Contains("duplicad", StringComparison.OrdinalIgnoreCase)
            || t.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
        static bool HasPage(string t) => t.Contains("OFFSET", StringComparison.OrdinalIgnoreCase)
            || t.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Skip", StringComparison.OrdinalIgnoreCase)
            || t.Contains("pageSize", StringComparison.OrdinalIgnoreCase);
        var (text, status) = await Ask("customer", prompt,
            t => t.Length > 500 && StubReason(t) == "" && Has409(t) && HasPage(t));
        TestContext.Check(status == "completed", "customer completed");
        TestContext.Check(text.Length > 500, "customer com codigo util");
        CheckNoStub(text, "customer");
        bool has409 = Has409(text);
        bool hasPage = HasPage(text);
        if (!(has409 && hasPage)) FailDump("customer", text);
        TestContext.Check(has409 && hasPage, "customer com conflito+paginacao");
        Console.WriteLine($"  [info] customer paginacao={text.Contains("pageSize")} email={text.Contains("Email")}");
    }

    public static async Task CrudSaleInvoice()
    {
        // CRUD composto header+detail: SaleInvoice com itens em transacao.
        const string prompt =
            "C# .NET 9 + Dapper + Postgres: POST /api/saleinvoices cria SaleInvoice (header: CustomerId, Date) + Items " +
            "(detail: ProductId, Qty, UnitPrice) em UMA transacao (BEGIN/COMMIT/ROLLBACK): total = soma(Qty*UnitPrice), valida estoque " +
            "suficiente de cada produto (409 se faltar, com rollback total) e da baixa no estoque no mesmo commit. " +
            "GET /api/saleinvoices/{id} retorna header + items. Mostre: DDL das 2 tabelas e o que acontece " +
            "se o 3o item estourar o estoque. So codigo + 3 bullets.";
        // Detalhe pode vir como Items, SaleInvoiceItems ou Item: o que importa e
        // a linha de detalhe existir (ProductId + Qty/Quantity) no codigo.
        static bool HasDetail(string t) => t.Contains("Items", StringComparison.OrdinalIgnoreCase)
            || (t.Contains("ProductId", StringComparison.OrdinalIgnoreCase)
                && (t.Contains("Qty", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("Quantity", StringComparison.OrdinalIgnoreCase)));
        static bool Transacional(string t) => t.Contains("BeginTransaction", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Transaction", StringComparison.OrdinalIgnoreCase);
        var (text, status) = await Ask("saleinvoice", prompt,
            t => t.Length > 600 && StubReason(t) == "" && Transacional(t) && HasDetail(t));
        TestContext.Check(status == "completed", "saleinvoice completed");
        TestContext.Check(text.Length > 600, "saleinvoice com codigo util");
        CheckNoStub(text, "saleinvoice");
        bool transacional = text.Contains("BeginTransaction", StringComparison.OrdinalIgnoreCase) || text.Contains("Transaction", StringComparison.OrdinalIgnoreCase);
        if (!transacional) FailDump("saleinvoice", text);
        TestContext.Check(transacional, "saleinvoice transacional");
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
        const string prompt =
            "C# .NET 9 + Postgres BackgroundService: worker que drena a tabela outbox (payload jsonb) em lote de 50 a cada 5s, publica no " +
            "barramento com retry exponencial + jitter (max 5 tentativas, depois dead-letter), respeita " +
            "CancellationToken em todo await (stop gracioso sem perder a mensagem da vez) e registra throughput " +
            "no log a cada minuto. Entregue o worker completo + DDL da dead-letter. So codigo + 3 bullets.";
        static bool Lifecycle(string t) => t.Contains("BackgroundService") && t.Contains("CancellationToken");
        var (text, status) = await Ask("worker", prompt,
            t => t.Length > 500 && StubReason(t) == "" && Lifecycle(t));
        TestContext.Check(status == "completed", "worker completed");
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
        const string prompt =
            "C# .NET 9 + Redis (StackExchange.Redis): repositorio de Product com cache-aside " +
            "(IDistributedCache, sliding 5min), fallback Postgres + repopula; PUT/DELETE invalidam a chave " +
            "com cache.RemoveAsync; stampede controlado por lock por chave e circuit-breaker simples (3 falhas) " +
            "quando o Redis cai. Entregue em DOIS blocos de codigo: (1) o repositorio de Product e " +
            "(2) o registro no DI no Program.cs (builder.Services.AddStackExchangeRedisCache + AddScoped " +
            "do repositorio). Diga em 3 bullets o que acontece quando o Redis esta fora.";
        // A API e escolha do autor (IDistributedCache do .NET ou o
        // ConnectionMultiplexer do StackExchange cru); o contrato e'
        // "cache + invalidacao da chave" (RemoveAsync / KeyDelete).
        static bool HasCache(string t) => t.Contains("IDistributedCache")
            || t.Contains("ConnectionMultiplexer", StringComparison.OrdinalIgnoreCase);
        static bool HasInvalidation(string t) => t.Contains("RemoveAsync")
            || t.Contains("KeyDelete", StringComparison.OrdinalIgnoreCase);
        // Registro no DI: API moderna, memory fallback, legada ou o registro de
        // servicos explicito (builder.Services.Add... / services.Add...).
        static bool HasDi(string t) => t.Contains("AddStackExchangeRedisCache") || t.Contains("AddDistributedMemoryCache")
            || t.Contains("AddDistributedRedisCache") || t.Contains("IConnectionMultiplexer")
            || t.Contains("builder.Services.Add", StringComparison.OrdinalIgnoreCase)
            || t.Contains("services.Add", StringComparison.OrdinalIgnoreCase);
        var (text, status) = await Ask("redis", prompt,
            t => t.Length > 500 && StubReason(t) == "" && HasCache(t) && HasInvalidation(t) && HasDi(t));
        TestContext.Check(status == "completed", "redis completed");
        TestContext.Check(text.Length > 500, "redis com codigo util");
        CheckNoStub(text, "redis");
        bool hasCache = HasCache(text);
        bool hasInvalidation = HasInvalidation(text);
        if (!(hasCache && hasInvalidation)) FailDump("redis", text);
        TestContext.Check(hasCache && hasInvalidation, "redis cache+invalidacao");
        bool di = HasDi(text);
        if (!di) FailDump("redis-di", text);
        TestContext.Check(di, "redis registro no DI");
        string diApi = text.Contains("AddStackExchangeRedisCache") ? "modern"
            : text.Contains("AddDistributedRedisCache") ? "legacy" : "custom";
        Console.WriteLine($"  [info] redis sliding={text.Contains("Sliding", StringComparison.OrdinalIgnoreCase)} circuito={text.Contains("circuit", StringComparison.OrdinalIgnoreCase)} di={diApi}");
    }

    public static async Task ReactDashboard()
    {
        // React + dashboard consumindo o ASP.NET: cards, grafico e estados.
        const string prompt =
            "React 19 + TS + Tailwind: Dashboard que consome GET /api/saleinvoices?days=30 do ASP.NET e exibe: " +
            "3 cards (faturamento, ticket medio, itens vendidos), grafico de barras por dia (SVG proprio, sem lib), " +
            "tabela das 10 maiores notas com link p/ detalhe, skeleton durante load, erro com retry por widget " +
            "independente e AbortController no filtro de periodo. Entregue o codigo .tsx completo + 3 bullets.";
        // Ciclo de vida: cancelamento do fetch + hook de efeito (useEffect/useLayoutEffect/use
        // da React 19) — nao amarrado a um nome unico de hook.
        static bool Lifecycle(string t) => t.Contains("AbortController")
            && (t.Contains("useEffect") || t.Contains("useLayoutEffect") || t.Contains("use("));
        var (text, status) = await Ask("dashboard", prompt,
            t => t.Length > 500 && StubReason(t) == "" && Lifecycle(t));
        TestContext.Check(status == "completed", "dashboard completed");
        TestContext.Check(text.Length > 500, "dashboard com codigo util");
        CheckNoStub(text, "dashboard");
        bool hasLifecycle = Lifecycle(text);
        TestContext.Check(hasLifecycle, "dashboard com ciclo de vida");
        Console.WriteLine($"  [info] dashboard cards={text.Contains("card", StringComparison.OrdinalIgnoreCase)} skeleton={text.Contains("skeleton", StringComparison.OrdinalIgnoreCase)}");
    }
}
