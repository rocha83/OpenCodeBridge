using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Help curado por tool, anexado à descrição do schema enviado ao modelo.
/// Complementa o schema (nomes/tipos) com o contrato de uso que o modelo erra
/// com mais frequência: quando usar, restrições e um exemplo mínimo.
/// </summary>
/// <remarks>
/// O texto é intencionalmente curto: o array de tools viaja em toda chamada.
/// Não é o `--help` bruto do CLI (que cita flags inexistentes e infla o JSON);
/// é o subconjunto suportado pela bridge. O marcador evita duplicação quando
/// o mesmo array passa por Responses, chat e/ou retry.
/// </remarks>
public static class ToolHelp
{
    /// <summary>Marcador de idempotência do help injetado.</summary>
    public const string Marker = "[uso-bridge]";

    /// <summary>
    /// Regra ÚNICA dos exemplos, injetada UMA vez na cauda de tools (nunca por
    /// tool: o array viaja em toda chamada). O 4B copia o literal do exemplo como
    /// se fosse o dado do pedido — em 2026-11-06 o `notas/hoje.txt` do help foi
    /// parar no `path` do git e todos os commits falharam. Duas defesas juntas:
    /// esta frase diz que o valor do exemplo é fictício, e o exemplo só usa
    /// caminho válido em qualquer workspace (`./`) ou arquivo que ele mesmo cria.
    /// </summary>
    public const string ExampleRule =
        "EXEMPLOS dos helps = FORMA, nao conteudo: nome/caminho/URL do exemplo e' ficticio e NAO e' o do pedido; copie a forma e troque pelos valores do seu pedido.";

    /// <summary>Todos os helps curados (os units varrem a classe inteira daqui).</summary>
    public static IReadOnlyDictionary<string, string> All => Docs;

    /// <summary>
    /// Fluxo git PASSO A PASSO: fonte ÚNICA do help da tool e da cutucada de
    /// continuacao. O 4B encerra o turno depois do `add` (medido 2026-11-06: 3/3
    /// tentativas sem `commit`); a frase que o empurra sai daqui, nunca de uma
    /// copia escrita a mao no teste. Path `./` e arquivo criado no proprio fluxo:
    /// copiar a letra produz acao valida em qualquer workspace.
    /// </summary>
    static readonly (string Key, string Call)[] GitFlow =
    {
        ("init", "git {\"command\":\"init\",\"path\":\"./\"}"),
        ("write", "write do arquivo que o pedido nomeia (conteudo = o do pedido)"),
        ("user", "git {\"command\":\"config user.name \\\"seu nome\\\"\",\"path\":\"./\"}"),
        ("email", "git {\"command\":\"config user.email \\\"voce@exemplo.com\\\"\",\"path\":\"./\"}"),
        ("add", "git {\"command\":\"add .\",\"path\":\"./\"}"),
        ("commit", "git {\"command\":\"commit -m \\\"mensagem do pedido\\\"\",\"path\":\"./\"}"),
        ("log", "git {\"command\":\"log --oneline\",\"path\":\"./\"} (prova)"),
    };

    static string GitCall(string key) => Array.Find(GitFlow, s => s.Key == key).Call;

    /// <summary>Fluxo git numerado, como vai no help da tool.</summary>
    public static string GitFlowText =>
        string.Join(" ", GitFlow.Select((s, i) => $"{i + 1}) {s.Call}"));

    /// <summary>
    /// Proximo passo do fluxo git depois do subcomando ja executado ("" = sem
    /// proximo). O texto vem de <see cref="GitFlow"/>: help e cutucada nao
    /// divergem na primeira edicao.
    /// </summary>
    /// <param name="subcommand">Subcomando git executado (init|add|commit|config).</param>
    public static string NextGitStep(string? subcommand) => (subcommand ?? "").Trim().ToLowerInvariant() switch
    {
        "init" => GitCall("write") + " -> " + GitCall("user") + " -> " + GitCall("email"),
        "config" => GitCall("add"),
        "add" => GitCall("commit") + " -> " + GitCall("log"),
        "commit" => GitCall("log"),
        _ => ""
    };

    /// <summary>Shell/bash/sh: mesmo contrato (uma chamada = um comando), mesma fonte.</summary>
    const string ShellText = """USO: uma chamada = um comando; o cwd de cada chamada e' a RAIZ do workspace (subpasta: `cd dir && cmd`, vale so nesta chamada); script = write + `bash arquivo`; pacote: veja ZIP no fim. AO CODIFICAR: monte o arquivo com write/edit e so entao use shell para build/teste/versionamento. DOTNET: com mais de um projeto, `--project` aponta o ARQUIVO do .csproj/.fsproj (a pasta e' ambigua); `dotnet new` so em pasta vazia. C# QUE COMPILA DE PRIMEIRA: membro de tipo publico tambem e' `public`; comparacao de generico = `EqualityComparer<T>.Default.Equals`; variavel lida fora do laco e' declarada ANTES do `for`. EX.: {"command":"ls -la"} · {"command":"dotnet build --project App.csproj"} · {"command":"dotnet test"}. ZIP (fluxo completo): 1) empacotar {"command":"zip -qr pacote.zip pasta"} (o .zip e' o 1o argumento, depois o que entra; -r recursivo, -q silencioso) 2) listar {"command":"unzip -l pacote.zip"} 3) testar {"command":"unzip -t pacote.zip"} 4) extrair {"command":"unzip -o pacote.zip -d destino"} (-o sobrescreve; sem -d extrai no cwd) 5) conferir {"command":"ls -la destino"}. Excluir do pacote: {"command":"zip -qr pacote.zip pasta -x \"*.log\""}. Erro `Zip file structure invalid` = o 1o argumento nao e' .zip valido; `zip warning: name not matched` = o caminho de origem nao existe.""";

    static readonly Dictionary<string, string> Docs = new(StringComparer.OrdinalIgnoreCase)
    {
                ["shell"] = ShellText,
                ["bash"] = ShellText,
                ["sh"] = ShellText,
        ["write"] = """USO: `path` e' relativo a RAIZ do workspace (o `cd` do shell NAO vale para tools): arquivo da subpasta = `sub/arquivo`. Cria ou reescreve o arquivo INTEIRO; mudanca pontual = edit. Pedido que manda criar/gravar arquivo entrega via write — devolver o codigo em prosa no chat NAO conclui. JSON: quebra de linha = \n (barra + n, dois caracteres) e aspas = \" ; nunca quebre a linha crua dentro da string. Arquivo grande: write curto + edit para completar. EX.: {"path":"data.txt","content":"linha1\nlinha2"} -> depois `read` para a prova""",
        ["read"] = """USO: `path` relativo a RAIZ do workspace (o `cd` do shell NAO vale para tools); leia ANTES de editar; arquivo grande = offset/limit quando o schema expuser; binario = shell+python3. PROVA: depois de write/edit, `read` o arquivo — o conteudo devolvido e' o que esta no disco; no historico, cole a saida literal. EX.: {"path":"data.txt"} · {"path":"Program.cs","offset":1,"limit":80}""",
        ["edit"] = """USO: `path` relativo a RAIZ do workspace (`sub/arquivo`; o `cd` do shell NAO vale para tools); `oldString` = trecho EXATO que JA existe no arquivo (uma linha curta e' mais facil de acertar). ACRESCENTAR: edit {"path":"data.txt","oldString":"2026-11-06","newString":"2026-11-06\nARQUIVO EDITADO - Clima: 25.4°C"} — o oldString reaparece INTEIRO dentro do newString. NAO SABE a linha exata? `read` primeiro e copie a linha real; sem ancora, use `write` com o conteudo atual + a linha nova. `oldString` nulo/vazio e' recusado. JSON: quebra de linha = \n e aspas = \". Confirme com `read` DEPOIS.""",
        ["glob"] = """USO: `pattern` so glob (sem regex/alternancia); diretorio em `path`; 0 matches = use shell `ls`/`find`. EX.: {"pattern":"**/*.cs","path":"."} · {"pattern":"*.md"}""",
        ["grep"] = """USO: `pattern` e' regex; restrinja com `path`/`include`; use read para o contexto. EX.: {"pattern":"TODO","path":"."} · {"pattern":"class \w+","include":"*.cs"}""",
        ["git"] = "FORMAS (uma por chamada, mesmo `path`): " + Tools.GitCommandSyntax.Canonical
            + "PASSOS (um por chamada, nesta ordem; config ANTES do primeiro `commit`): " + GitFlowText + ". "
            + "NAO USE: `--bare`, `--path`, `--allow-empty` (o gate recusa e ensina a forma). "
            + "UM COMMIT POR PASSO: cada passo que muda arquivo termina com `add .` + `commit -m \"<mensagem do passo>\"`. "
            + "ERROS: `Author identity unknown` = falta o config; `nothing to commit` = arquivo mudou depois do add.",
        ["subagent"] = """USO: um micro-enunciado por call (OBJETIVO+ESCOPO+PROIBIDO+ACEITE); `prompt` completo; nao aninhe JSON; use a tool nativa, nunca shell/execute. Preserve o array `tools` e o system do role ao delegar; nao troque schema por prosa. EX.: {"description":"fix","prompt":"OBJETIVO: ... ESCOPO: ... PROIBIDO: ... ACEITE: ..."} (opcional: `model` com sufixo de role)""",
        ["task"] = """USO: um micro-enunciado por call (OBJETIVO+ESCOPO+PROIBIDO+ACEITE); `prompt` completo; nao aninhe JSON; use a tool nativa, nunca shell/execute. Preserve o array `tools` e o system do role ao delegar; nao troque schema por prosa. EX.: {"description":"fix","prompt":"OBJETIVO: ... ESCOPO: ... PROIBIDO: ... ACEITE: ..."} (opcional: `model` com sufixo de role)""",
        ["question"] = """USO: só para ambiguidade real, 1 rodada; 2–4 opções curtas; não use para confirmar ação já decidida. Ex.: {"questions":[{"question":"...","header":"...","options":[...]}]}.""",
        ["skill"] = """USO: `id` exato de skill disponível; carregue só quando a tarefa casar; não use para ler arquivo comum. Ex.: {"id":"opencode"}.""",
        ["webfetch"] = """USO: URL http(s), read-only; a bridge envia User-Agent de Firefox 133 em Windows 11 (evita bloqueio de bot). `format` text/markdown/html. Use para obter texto REAL da rede (clima, docs, paginas) e so reporte o que a tool devolveu; nao invente conteudo. EX.: {"url":"https://example.com","format":"text"} · {"url":"https://example.com/docs","format":"markdown"} (URL do exemplo e' fictícia: use a do pedido)""",
        ["websearch"] = """USO: `query` curta para informação atual; não use para arquivo local. Ex.: {"query":"..."}.""",
        ["execute"] = """USO: JS Code Mode; sem import/fs/timers; chame `tools.X`; `return` explícito; a bridge desembrulha tools.X. Ex.: `return tools.read({path:"a"})`.""",
        ["fetch"] = """USO: GET read-only; prefira webfetch quando existir. Ex.: {"url":"https://example.com"}.""",
        ["display"] = """USO: saída curta para o usuário; não substitui write/read; role display só permite echo. Ex.: {"command":"echo ok"}.""",
        ["proc"] = """USO: operação de processo; exige role/permissão; não use shell para contornar bloqueio. Ex.: {"command":"ps"}.""",
        ["install"] = """USO: instalação de dependência; exige role/permissão; não use para contornar bloqueio. Ex.: {"command":"dotnet restore"}.""",
    };

    /// <summary>
    /// Help curado de UMA tool (o mesmo texto que vai na descricao do schema) ou "".
    /// Fonte unica: quem precisa repetir a instrucao FORA do array de tools (ex.: o
    /// aviso de salvage do <see cref="ToolCallRepair"/>) chama aqui — nao copia texto.
    /// </summary>
    /// <param name="name">Nome canonico da tool.</param>
    /// <returns>Texto do help (sem o marcador) ou "".</returns>
    public static string For(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Docs.TryGetValue(name, out string? doc) ? doc.Trim() : "";

    /// <summary>Help curado de varias tools, sem repetir o mesmo texto.</summary>
    /// <param name="names">Nomes canonicos (pode vir nulo/vazio).</param>
    /// <returns>Blocos de help separados por linha em branco.</returns>
    public static string For(IEnumerable<string>? names)
    {
        if (names is null) return "";
        var docs = new List<string>();
        foreach (string name in names)
        {
            string doc = For(name);
            if (doc.Length > 0 && !docs.Contains(doc)) docs.Add(doc);
        }
        return string.Join("\n\n", docs);
    }

    /// <summary>Aplica o help ao array e devolve o mesmo array (mutado no lugar).</summary>
    /// <param name="tools">Array de tools nos shapes chat, Responses ou namespace.</param>
    /// <returns>O próprio array, para encadear no construtor do catálogo.</returns>
    public static JsonArray Apply(JsonArray tools)
    {
        ApplyTo(tools);
        return tools;
    }

    /// <summary>Aplica o help a qualquer nó de tools, incluindo namespaces aninhados.</summary>
    /// <param name="tools">Nó de tools; não-array é ignorado.</param>
    public static void ApplyTo(JsonNode? tools)
    {
        if (tools is not JsonArray array) return;
        foreach (var node in array) ApplyTool(node);
    }

    static void ApplyTool(JsonNode? node)
    {
        if (node is not JsonObject obj) return;

        if (obj["type"]?.GetValue<string>() == "namespace" && obj["tools"] is JsonArray nested)
        {
            ApplyTo(nested);
            return;
        }

        if (obj["function"] is JsonObject fn)
        {
            string? fnName = fn["name"]?.GetValue<string>() ?? obj["name"]?.GetValue<string>();
            ApplyDescription(fn, fnName);
            return;
        }

        ApplyDescription(obj, obj["name"]?.GetValue<string>());
    }

    static void ApplyDescription(JsonObject tool, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Docs.TryGetValue(name, out string? doc)) return;

        string current = tool["description"] is JsonValue value && value.TryGetValue<string>(out string? text)
            ? text
            : "";
        if (current.Contains(Marker, StringComparison.Ordinal)) return;

        tool["description"] = (current.Length > 0 ? current.TrimEnd() + "\n\n" : "") + Marker + " " + doc;
    }
}
