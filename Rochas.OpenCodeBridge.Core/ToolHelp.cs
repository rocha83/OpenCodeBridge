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
        ["shell"] = """USO: uma chamada = um comando; o cwd de cada chamada é a raiz do workspace (para rodar numa subpasta: `cd dir && cmd`, que vale nesta chamada); script = write + `bash arquivo`; pacote = `zip -qr pacote.zip .` (o nome do .zip vem primeiro; erro `Zip file structure invalid` = o primeiro argumento não é um .zip válido — use um nome novo); a saída real da tool é a prova. Ex.: {"command":"ls -la"}.""",
        ["bash"] = """USO: uma chamada = um comando; o cwd de cada chamada é a raiz do workspace (para rodar numa subpasta: `cd dir && cmd`, que vale nesta chamada); script = write + `bash arquivo`; pacote = `zip -qr pacote.zip .` (o nome do .zip vem primeiro; erro `Zip file structure invalid` = o primeiro argumento não é um .zip válido — use um nome novo); a saída real da tool é a prova. Ex.: {"command":"ls -la"}.""",
        ["sh"] = """USO: uma chamada = um comando; o cwd de cada chamada é a raiz do workspace (para rodar numa subpasta: `cd dir && cmd`, que vale nesta chamada); script = write + `bash arquivo`; pacote = `zip -qr pacote.zip .` (o nome do .zip vem primeiro; erro `Zip file structure invalid` = o primeiro argumento não é um .zip válido — use um nome novo); a saída real da tool é a prova. Ex.: {"command":"ls -la"}.""",
        ["write"] = """USO: `path` é relativo à RAIZ do workspace (o `cd` do shell NAO vale para tools): arquivo da subpasta = `sub/arquivo`; arquivo novo ou reescrita integral; mudança pontual = edit. Ex.: {"path":"a.txt","content":"..."}.""",
        ["read"] = """USO: `path` é relativo à RAIZ do workspace (o `cd` do shell NAO vale para tools): arquivo da subpasta = `sub/arquivo`; leia antes de editar; arquivo grande = offset/limit quando o schema expuser; binário = shell+python3. Ex.: {"path":"a.txt"}.""",
        ["edit"] = """USO: `path` é relativo à RAIZ do workspace (o `cd` do shell NAO vale para tools): arquivo da subpasta = `sub/arquivo` (não `arquivo`); `oldString` exato e único; para ACRESCENTAR, `newString` = oldString + trecho novo, e o `oldString` precisa reaparecer intacto dentro do `newString`; se a intenção era preservar um trecho, confirme com read/grep DEPOIS da edição. Ex.: {"path":"a.txt","oldString":"x","newString":"y"}.""",
        ["glob"] = """USO: `pattern` só glob (sem regex/alternância); diretório em `path`; 0 matches = `ls`/`find` no shell. Ex.: {"pattern":"**/*.cs","path":"src"}.""",
        ["grep"] = """USO: `pattern` é regex; restrinja com `path`/`include` quando houver; use read para contexto. Ex.: {"pattern":"TODO","path":"src"}.""",
        ["git"] = """
            EXEMPLO DE SUCESSO (nomes entre `<>` são fictícios: troque pelos do seu pedido). Uma suboperação por chamada; o MESMO `path` em todas.
             // 1) cria a pasta de trabalho
             shell {"command":"mkdir -p <pasta>"}
             // 2) inicia o repo, apontando a pasta
             git {"command":"init","path":"<pasta>"}
             // 3) grava os arquivos já com o nome da pasta no path
             write {"path":"<pasta>/<arquivo>","content":"..."}
             // 4) ANTES do primeiro `commit`: identidade local, DUAS chamadas nesta ordem, mesma pasta
             git {"command":"config user.name \"<nome>\"","path":"<pasta>"}
             git {"command":"config user.email \"<email>\"","path":"<pasta>"}
             // 5) adiciona tudo
             git {"command":"add .","path":"<pasta>"}
             // 6) commita com a mensagem
             git {"command":"commit -m \"<mensagem>\"","path":"<pasta>"}
             // 7) confere: os commits aparecem no log (cole esta saída no histórico)
             git {"command":"log --oneline","path":"<pasta>"}
            LEITURA: status, log, diff, show, branch, ls-files, rev-parse. Erro `Author identity unknown` (ou `Please tell me who you are`) = o passo 4 ainda não rodou.
            """,
        ["subagent"] = """USO: um micro-enunciado por call (OBJETIVO+ESCOPO+PROIBIDO+ACEITE); `prompt` completo; não aninhe JSON; use a tool nativa, nunca shell/execute. Ex.: {"agent":"build","description":"fix","prompt":"..."}.""",
        ["task"] = """USO: um micro-enunciado por call (OBJETIVO+ESCOPO+PROIBIDO+ACEITE); `prompt` completo; não aninhe JSON; use a tool nativa, nunca shell/execute. Ex.: {"agent":"build","description":"fix","prompt":"..."}.""",
        ["question"] = """USO: só para ambiguidade real, 1 rodada; 2–4 opções curtas; não use para confirmar ação já decidida. Ex.: {"questions":[{"question":"...","header":"...","options":[...]}]}.""",
        ["skill"] = """USO: `id` exato de skill disponível; carregue só quando a tarefa casar; não use para ler arquivo comum. Ex.: {"id":"opencode"}.""",
        ["webfetch"] = """USO: URL http(s), read-only; `format` text/markdown/html. Ex.: {"url":"https://...","format":"markdown"}.""",
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
