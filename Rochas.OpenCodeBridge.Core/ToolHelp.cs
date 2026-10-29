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
        ["shell"] = """USO: uma chamada = um comando; `cd` não persiste (use `cd dir && cmd`); script = write + `bash arquivo`; não simule saída. Ex.: {"command":"ls -la"}.""",
        ["bash"] = """USO: uma chamada = um comando; `cd` não persiste (use `cd dir && cmd`); script = write + `bash arquivo`; não simule saída. Ex.: {"command":"ls -la"}.""",
        ["sh"] = """USO: uma chamada = um comando; `cd` não persiste (use `cd dir && cmd`); script = write + `bash arquivo`; não simule saída. Ex.: {"command":"ls -la"}.""",
        ["write"] = """USO: arquivo novo ou reescrita integral; mudança pontual = edit; conteúdo completo, sem stub. Ex.: {"path":"a.txt","content":"..."}.""",
        ["read"] = """USO: leia antes de editar; arquivo grande = offset/limit quando o schema expuser; binário = shell+python3. Ex.: {"path":"a.txt"}.""",
        ["edit"] = """USO: `oldString` exato e único; para ACRESCENTAR, `newString` = oldString + trecho novo, e o `oldString` precisa reaparecer intacto dentro do `newString`; se a intenção era preservar um trecho, confirme com read/grep DEPOIS da edição. Ex.: {"path":"a.txt","oldString":"x","newString":"y"}.""",
        ["glob"] = """USO: `pattern` só glob (sem regex/alternância); diretório em `path`; 0 matches = `ls`/`find` no shell. Ex.: {"pattern":"**/*.cs","path":"src"}.""",
        ["grep"] = """USO: `pattern` é regex; restrinja com `path`/`include` quando houver; use read para contexto. Ex.: {"pattern":"TODO","path":"src"}.""",
        ["git"] = """USO: `path` = repo; `init` com dir explícito; uma suboperação por chamada; identidade via config user.name/email; não encadeie `&&`. Ex.: {"command":"status","path":"/repo"}.""",
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
