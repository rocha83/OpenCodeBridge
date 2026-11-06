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

    static readonly Dictionary<string, string> Docs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shell"] = """USO: uma chamada = um comando; o cwd de cada chamada e' a RAIZ do workspace (subpasta: `cd dir && cmd`, vale so nesta chamada); script = write + `bash arquivo`; pacote = `zip -qr pacote.zip .` (nome do .zip primeiro; `Zip file structure invalid` = o 1o argumento nao e' .zip valido). AO CODIFICAR: monte o arquivo com write/edit e so entao use shell para build/teste/versionamento. DOTNET: com mais de um projeto, `--project` aponta o ARQUIVO do .csproj/.fsproj (a pasta e' ambigua); `dotnet new` so em pasta vazia. C# QUE COMPILA DE PRIMEIRA: membro de tipo publico tambem e' `public`; comparacao de generico = `EqualityComparer<T>.Default.Equals`; variavel lida fora do laco e' declarada ANTES do `for`. EX.: {"command":"ls -la"} · {"command":"dotnet build notas/notas.csproj"} · {"command":"cd src && dotnet test"}""",
        ["bash"] = """USO: uma chamada = um comando; o cwd de cada chamada e' a RAIZ do workspace (subpasta: `cd dir && cmd`, vale so nesta chamada); script = write + `bash arquivo`; pacote = `zip -qr pacote.zip .` (nome do .zip primeiro; `Zip file structure invalid` = o 1o argumento nao e' .zip valido). AO CODIFICAR: monte o arquivo com write/edit e so entao use shell para build/teste/versionamento. DOTNET: com mais de um projeto, `--project` aponta o ARQUIVO do .csproj/.fsproj (a pasta e' ambigua); `dotnet new` so em pasta vazia. C# QUE COMPILA DE PRIMEIRA: membro de tipo publico tambem e' `public`; comparacao de generico = `EqualityComparer<T>.Default.Equals`; variavel lida fora do laco e' declarada ANTES do `for`. EX.: {"command":"ls -la"} · {"command":"dotnet build notas/notas.csproj"} · {"command":"cd src && dotnet test"}""",
        ["sh"] = """USO: uma chamada = um comando; o cwd de cada chamada e' a RAIZ do workspace (subpasta: `cd dir && cmd`, vale so nesta chamada); script = write + `bash arquivo`; pacote = `zip -qr pacote.zip .` (nome do .zip primeiro; `Zip file structure invalid` = o 1o argumento nao e' .zip valido). AO CODIFICAR: monte o arquivo com write/edit e so entao use shell para build/teste/versionamento. DOTNET: com mais de um projeto, `--project` aponta o ARQUIVO do .csproj/.fsproj (a pasta e' ambigua); `dotnet new` so em pasta vazia. C# QUE COMPILA DE PRIMEIRA: membro de tipo publico tambem e' `public`; comparacao de generico = `EqualityComparer<T>.Default.Equals`; variavel lida fora do laco e' declarada ANTES do `for`. EX.: {"command":"ls -la"} · {"command":"dotnet build notas/notas.csproj"} · {"command":"cd src && dotnet test"}""",
        ["write"] = """USO: `path` e' relativo a RAIZ do workspace (o `cd` do shell NAO vale para tools): arquivo da subpasta = `sub/arquivo`. Cria ou reescreve o arquivo INTEIRO; mudanca pontual = edit. Pedido que manda criar/gravar arquivo entrega via write — devolver o codigo em prosa no chat NAO conclui. JSON: quebra de linha = \n (barra + n, dois caracteres) e aspas = \" ; nunca quebre a linha crua dentro da string. Arquivo grande: write curto + edit para completar. EX.: {"path":"notas/hoje.txt","content":"linha1\nlinha2"} -> depois `read` para a prova""",
        ["read"] = """USO: `path` relativo a RAIZ do workspace (o `cd` do shell NAO vale para tools); leia ANTES de editar; arquivo grande = offset/limit quando o schema expuser; binario = shell+python3. PROVA: depois de write/edit, `read` o arquivo — o conteudo devolvido e' o que esta no disco; no historico, cole a saida literal. EX.: {"path":"notas/hoje.txt"} · {"path":"src/Program.cs","offset":1,"limit":80}""",
        ["edit"] = """USO: `path` relativo a RAIZ do workspace (`sub/arquivo`; o `cd` do shell NAO vale para tools); `oldString` = trecho EXATO que JA existe no arquivo (uma linha curta e' mais facil de acertar). ACRESCENTAR: edit {"path":"data.txt","oldString":"2026-11-06","newString":"2026-11-06\nARQUIVO EDITADO - Clima: 25.4°C"} — o oldString reaparece INTEIRO dentro do newString. NAO SABE a linha exata? `read` primeiro e copie a linha real; sem ancora, use `write` com o conteudo atual + a linha nova. `oldString` nulo/vazio e' recusado. JSON: quebra de linha = \n e aspas = \". Confirme com `read` DEPOIS.""",
        ["glob"] = """USO: `pattern` so glob (sem regex/alternancia); diretorio em `path`; 0 matches = use shell `ls`/`find`. EX.: {"pattern":"**/*.cs","path":"src"} · {"pattern":"*.md"}""",
        ["grep"] = """USO: `pattern` e' regex; restrinja com `path`/`include`; use read para o contexto. EX.: {"pattern":"TODO","path":"src"} · {"pattern":"class \w+","include":"*.cs"}""",
        ["git"] = "FORMAS (uma por chamada, mesmo `path`): " + Tools.GitCommandSyntax.Canonical
            + ". ORDEM: config user.name/email ANTES do primeiro `commit`; depois `add .`, `commit -m`, `log --oneline`. "
            + "NAO USE: `--bare`, `--path`, `--allow-empty` (o gate recusa e ensina a forma). "
            + "EXEMPLO DE SUCESSO (nomes do seu pedido): shell {\"command\":\"mkdir -p notas\"} -> git {\"command\":\"init\",\"path\":\"notas\"} -> "
            + "write {\"path\":\"notas/hoje.txt\",\"content\":\"...\"} -> git {\"command\":\"config user.name \\\"Ana\\\"\",\"path\":\"notas\"} -> git {\"command\":\"config user.email \\\"ana@exemplo.com\\\"\",\"path\":\"notas\"} -> git {\"command\":\"add .\",\"path\":\"notas\"} -> commit -m \"primeiro commit\" -> log --oneline. "
            + "UM COMMIT POR PASSO: cada passo que muda arquivo termina com `add .` + `commit -m \"<mensagem do passo>\"`. "
            + "ERROS: `Author identity unknown` = falta o config; `nothing to commit` = arquivo mudou depois do add.",
        ["subagent"] = """USO: um micro-enunciado por call (OBJETIVO+ESCOPO+PROIBIDO+ACEITE); `prompt` completo; nao aninhe JSON; use a tool nativa, nunca shell/execute. Preserve o array `tools` e o system do role ao delegar; nao troque schema por prosa. EX.: {"agent":"build","description":"fix","prompt":"OBJETIVO: ... ESCOPO: ... PROIBIDO: ... ACEITE: ..."}""",
        ["task"] = """USO: um micro-enunciado por call (OBJETIVO+ESCOPO+PROIBIDO+ACEITE); `prompt` completo; nao aninhe JSON; use a tool nativa, nunca shell/execute. Preserve o array `tools` e o system do role ao delegar; nao troque schema por prosa. EX.: {"agent":"build","description":"fix","prompt":"OBJETIVO: ... ESCOPO: ... PROIBIDO: ... ACEITE: ..."}""",
        ["question"] = """USO: só para ambiguidade real, 1 rodada; 2–4 opções curtas; não use para confirmar ação já decidida. Ex.: {"questions":[{"question":"...","header":"...","options":[...]}]}.""",
        ["skill"] = """USO: `id` exato de skill disponível; carregue só quando a tarefa casar; não use para ler arquivo comum. Ex.: {"id":"opencode"}.""",
        ["webfetch"] = """USO: URL http(s), read-only; a bridge envia User-Agent de Firefox 133 em Windows 11 (evita bloqueio de bot). `format` text/markdown/html. Use para obter texto REAL da rede (clima, docs, paginas) e so reporte o que a tool devolveu; nao invente conteudo. EX.: {"url":"https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m","format":"text"} · {"url":"https://...","format":"markdown"}""",
        ["websearch"] = """USO: `query` curta para informação atual; não use para arquivo local. Ex.: {"query":"..."}.""",
        ["execute"] = """USO: JS Code Mode; sem import/fs/timers; chame `tools.X`; `return` explícito; a bridge desembrulha tools.X. Ex.: `return tools.read({path:"a"})`.""",
        ["fetch"] = """USO: GET read-only; prefira webfetch quando existir. Ex.: {"url":"https://..."}.""",
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
