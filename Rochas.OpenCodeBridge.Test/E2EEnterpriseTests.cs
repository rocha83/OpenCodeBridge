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
    /// Motivo de stub (mesmas regras do <see cref="CheckNoStub"/>), para o
    /// contrato do cenario: vazio = sem stub.
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
    /// Amostras do step (WAR_ENT_SAMPLES, default 1). O default 1 E' o gate:
    /// uma amostra, contrato, temperatura baixa. N &gt; 1 e' medicao de
    /// estabilidade (opt-in), nunca "tentar ate passar".
    /// </summary>
    static int Samples => int.TryParse(Environment.GetEnvironmentVariable("WAR_ENT_SAMPLES"), out int n) && n > 0 ? n : 1;

    /// <summary>Amostras que precisam atender o contrato (WAR_ENT_MIN_PASS, default = Samples).</summary>
    static int MinPass
    {
        get
        {
            int k = int.TryParse(Environment.GetEnvironmentVariable("WAR_ENT_MIN_PASS"), out int v) && v > 0 ? v : Samples;
            return Math.Min(k, Samples);
        }
    }

    /// <summary>
    /// Pede o artefato em TEXTO e aplica o contrato do cenario. Com o default
    /// (WAR_ENT_SAMPLES=1) NAO ha retry: uma amostra, um veredito — o gate mede
    /// o comportamento em vez de esconder a variancia. Com N &gt; 1 (opt-in,
    /// diagnostico de estabilidade) roda N amostras FIXAS, exige
    /// WAR_ENT_MIN_PASS e loga a taxa; N nunca cresce ate passar. Abaixo do
    /// minimo devolve texto vazio para o assert reprovar (nao "quase passou").
    /// </summary>
    static async Task<(string Text, string Status)> Ask(string label, string prompt, Func<string, bool> contract)
    {
        int samples = Samples, minPass = MinPass;
        string last = "", firstPass = "", status = "";
        int pass = 0;
        for (int n = 1; n <= samples; n++)
        {
            var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", Req(prompt));
            status = r["status"]?.GetValue<string>() ?? "";
            if (status == "completed")
            {
                last = TextOf(r);
                bool ok = contract(last);
                if (ok) { pass++; if (firstPass.Length == 0) firstPass = last; }
                else if (samples == 1)
                {
                    // Uma amostra: devolve o texto REAL para o assert do cenario
                    // apontar o motivo exato (transacao? detalhe?) + fail-dump.
                    Console.WriteLine($"  [contract] {label}: nao atendido (len={last.Length})");
                    FailDump(label, last);
                }
            }
            if (samples > 1)
                Console.WriteLine($"  [stability] {label} amostra {n}/{samples}: status={status} len={last.Length} ok={pass}");
        }
        if (samples > 1)
            Console.WriteLine($"  [stability] {label} taxa={pass}/{samples} (minimo {minPass})");
        // Abaixo do minimo em N>1 e' reprovacao explicita (nao "quase passou").
        if (samples > 1 && pass < minPass)
            TestContext.Check(false, $"{label} estabilidade {pass}/{samples} < minimo {minPass}");
        return (samples == 1 ? last : firstPass, status);
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
            "GET /api/saleinvoices/{id} retorna header + items. Entregue o codigo COMPLETO: DDL das 2 tabelas + " +
            "o handler POST inteiro (BeginTransaction/Commit/Rollback) + o SELECT do GET; nao resuma nem use elipses. " +
            "Mostre tambem o que acontece se o 3o item estourar o estoque. So codigo + 3 bullets.";
        // Detalhe pode vir como Items, SaleInvoiceItems ou Item: o que importa e
        // a linha de detalhe existir (ProductId + Qty/Quantity) no codigo.
        static bool HasDetail(string t) => t.Contains("Items", StringComparison.OrdinalIgnoreCase)
            || (t.Contains("ProductId", StringComparison.OrdinalIgnoreCase)
                && (t.Contains("Qty", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("Quantity", StringComparison.OrdinalIgnoreCase)));
        static bool Transacional(string t) => t.Contains("BeginTransaction", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Transaction", StringComparison.OrdinalIgnoreCase);
        var (text, status) = await Ask("saleinvoice", prompt,
            t => t.Length > 1200 && StubReason(t) == "" && Transacional(t) && HasDetail(t));
        TestContext.Check(status == "completed", "saleinvoice completed");
        TestContext.Check(text.Length > 1200, "saleinvoice com codigo util");
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

    // --- Redis fatiado em DOIS steps (2026-11-07) ---------------------------
    // Ate aqui o cenario pedia DOIS blocos no mesmo texto (repositorio + DI) e
    // media os tres contratos de uma vez: medido 2 FAILs em 3 rodadas
    // (`len=3199` e `len=5126`, sem `RemoveAsync` — a invalidacao e o bloco (2)
    // ficavam depois do ponto em que o texto terminava) contra PASS isolado de
    // 22,4s/22,9s. Mesmo remedio do E-tools-full: um artefato por step e a
    // continuidade pelo handoff do artefato. O bloco (1) fica em disco e vira
    // contexto do bloco (2) — o segundo step continua o primeiro de verdade.

    /// <summary>Bloco (1) do Redis (repositorio) — contexto do bloco (2).</summary>
    const string RedisHandoffPath = "/tmp/opencode/redis-chain/block1.md";

    static void WriteRedisHandoff(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RedisHandoffPath)!);
        File.WriteAllText(RedisHandoffPath, text);
    }

    static string? ReadRedisHandoff()
    {
        try { return File.Exists(RedisHandoffPath) ? File.ReadAllText(RedisHandoffPath).Trim() : null; }
        catch (IOException) { return null; }
    }

    // A API de cache e' escolha do autor: IDistributedCache
    // (`Remove`/`RemoveAsync`) ou o ConnectionMultiplexer do StackExchange
    // (`KeyDelete`). As tres APAGAM a chave — o que nao vale e' so descrever a
    // invalidacao em prosa sem a chamada no codigo.
    static bool RedisHasCache(string t) => t.Contains("IDistributedCache")
        || t.Contains("ConnectionMultiplexer", StringComparison.OrdinalIgnoreCase);

    static bool RedisHasInvalidation(string t) => t.Contains("RemoveAsync")
        || t.Contains("Remove(")
        || t.Contains("KeyDelete", StringComparison.OrdinalIgnoreCase);

    /// <summary>Step WAR `e-topics-ent-redis-cache`: bloco (1), o repositorio cache-aside.</summary>
    public static async Task RedisCacheAside()
    {
        // A linha E-ent e' CONCEITUAL: texto puro via /v1/responses (o modelo
        // entrega o codigo no texto). O caminho de tool (write em disco, git,
        // shell) fica nas trilhas E-tools — aqui nao se pede call.
        const string prompt =
            "C# .NET 9 + Redis (StackExchange.Redis): repositorio ProductRepository com cache-aside " +
            "(IDistributedCache, sliding 5min): GetAsync le do cache, em miss cai no Postgres (Dapper) e repopula; " +
            "SetAsync e DeleteAsync do repositorio INVALIDAM a chave com cache.RemoveAsync; stampede controlado " +
            "por lock por chave e circuit-breaker simples (3 falhas) quando o Redis cai. Entregue SO O BLOCO (1): " +
            "a classe do repositorio COMPLETA (usings, construtor, GetAsync, SetAsync, DeleteAsync com RemoveAsync, " +
            "lock e breaker) + 3 bullets do que acontece quando o Redis esta fora.";
        var (text, status) = await Ask("redis-cache", prompt,
            t => t.Length > 500 && StubReason(t) == "" && RedisHasCache(t) && RedisHasInvalidation(t));
        TestContext.Check(status == "completed", "redis-cache completed");
        TestContext.Check(text.Length > 500, "redis-cache com codigo util");
        CheckNoStub(text, "redis-cache");
        bool hasCache = RedisHasCache(text);
        bool hasInvalidation = RedisHasInvalidation(text);
        if (!(hasCache && hasInvalidation)) FailDump("redis-cache", text);
        // Handoff antes do assert: o step seguinte precisa do bloco (1) mesmo que
        // este veredito seja FAIL (a causa do FAIL fica no log do step).
        if (hasCache && hasInvalidation) WriteRedisHandoff(text);
        TestContext.Check(hasCache && hasInvalidation, "redis-cache cache+invalidacao");
        Console.WriteLine($"  [info] redis-cache sliding={text.Contains("Sliding", StringComparison.OrdinalIgnoreCase)} " +
            $"lock={text.Contains("lock", StringComparison.OrdinalIgnoreCase)} " +
            $"circuito={text.Contains("circuit", StringComparison.OrdinalIgnoreCase)} handoff={RedisHandoffPath}");
    }

    /// <summary>
    /// Step WAR `e-topics-ent-redis-di`: continua o bloco (1) do
    /// `e-topics-ent-redis-cache` (handoff em disco) e entrega SO o bloco (2) —
    /// o registro no DI. Sem o bloco (1) o step falha com a causa exata: nao
    /// existe "refaca o repositorio aqui".
    /// </summary>
    public static async Task RedisDi()
    {
        string? repo = ReadRedisHandoff();
        if (string.IsNullOrWhiteSpace(repo))
            throw new Exception("sem bloco (1) do redis: rode e-topics-ent-redis-cache antes (handoff " +
                RedisHandoffPath + ")");
        const string head =
            "CONTINUACAO do step anterior. O bloco (1) ja esta pronto (repositorio ProductRepository com " +
            "IDistributedCache e cache.RemoveAsync) e vai abaixo como CONTEXTO — NAO reescreva, NAO repita e NAO " +
            "resuma esse repositorio. Entregue SO O BLOCO (2): o trecho de Program.cs de uma Minimal API .NET 9 que " +
            "registra Redis e repositorio no DI, exatamente com builder.Services.AddStackExchangeRedisCache(options => { " +
            "options.Configuration = builder.Configuration.GetConnectionString(\"Redis\"); options.InstanceName = " +
            "\"ProductCache:\"; }) e builder.Services.AddScoped<IProductRepository, ProductRepository>(); (o construtor " +
            "recebe IDistributedCache e a connection string do Postgres). Formato: comecar a resposta com ```csharp e a " +
            "linha var builder = WebApplication.CreateBuilder(args);, sem explicacao antes do codigo, e terminar com " +
            "3 bullets de configuracao/ambiente (ConnectionStrings:Redis, InstanceName, Redis fora).";
        // Contrato do bloco (2): registro de Redis no DI + repositorio scoped.
        // `builder.Services.Add` generico NAO prova Redis e nao fecha o contrato.
        static bool HasRedisDi(string t) => t.Contains("AddStackExchangeRedisCache")
            || t.Contains("AddDistributedRedisCache") || t.Contains("IConnectionMultiplexer");
        static bool HasScoped(string t) => t.Contains("AddScoped")
            && (t.Contains("ProductRepository") || t.Contains("IProductRepository"));
        // Ordem deliberada: o artefato do step anterior vem PRIMEIRO e a
        // instrucao por ULTIMO (o que esta colado na geracao e' o "entregue so
        // o bloco (2)"), senao o modelo recomeca o repositorio — medido.
        var (text, status) = await Ask("redis-di",
            "--- REPOSITORIO DO STEP ANTERIOR (bloco 1, CONTEXTO) ---\n" + repo +
            "\n--- FIM DO CONTEXTO ---\n\n" + head,
            t => t.Length > 300 && StubReason(t) == "" && HasRedisDi(t) && HasScoped(t));
        TestContext.Check(status == "completed", "redis-di completed");
        TestContext.Check(text.Length > 300, "redis-di com trecho util");
        CheckNoStub(text, "redis-di");
        bool redisDi = HasRedisDi(text);
        bool scoped = HasScoped(text);
        if (!(redisDi && scoped)) FailDump("redis-di", text);
        TestContext.Check(redisDi, "redis-di registro do Redis");
        TestContext.Check(scoped, "redis-di repositorio scoped");
        string api = text.Contains("AddStackExchangeRedisCache") ? "modern"
            : text.Contains("AddDistributedRedisCache") ? "legacy" : "multiplexer";
        Console.WriteLine($"  [info] redis-di api={api} scoped={scoped} contexto={repo.Length} chars");
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
