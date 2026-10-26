using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Web.Services;

// Tool definitions for the bridge (chat/completions format).
// Descrições próprias curtas + help curado do Core (ToolHelp); cada request
// carrega o array e o help viaja junto para reduzir erro de uso. Retorna
// instância nova por chamada (JsonNode tem um só pai — cache estático quebraria o loop).
public static class ToolDefinitions
{
    // Harness anunciável: escrita (write/edit) inclusa — o filtro é o CSV do
    // agente (`Allowed`) + retaguarda de modo no executor (plan barra escrita).
    // Cada request carrega o array (~500 tokens com escrita); descrições curtas.
    public static JsonArray GetTools(string? mode = null)
    {
        var tools = new JsonArray();
        foreach (var def in Catalog)
        {
            // Code Mode (`execute`) e estrategia legada; a Web usa tools diretas.
            if (string.Equals(def.Name, "execute", StringComparison.OrdinalIgnoreCase)) continue;
            tools.Add(BuildTool(def));
        }
        return ToolHelp.Apply(tools);
    }

    // Harness por agente: catálogo CHEIO por padrão; o CSV é DENYLIST.
    // `*` (ou `all`) = todas as tools do catálogo; `-nome` nega uma tool;
    // `display` libera shell só-echo (ver DisplayOnly) + leitura via read.
    // CSV sem `*` continua lista explícita (compatibilidade das linhas antigas).
    public static bool Allowed(Models.Agent? a, string name)
    {
        if (a is null) return false;
        var set = ToolSet(a);
        if (set.Contains("-" + name)) return false;
        if (set.Contains("*") || set.Contains("all")) return true;
        if (set.Contains(name)) return true;
        return name == "shell" && set.Contains("display");
    }

    public static HashSet<string> ToolSet(Models.Agent? a) =>
        new((a?.Tools ?? "").Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    // Exibição em console: só `echo ...` simples (sem pipes/redirects).
    public static bool DisplayOnly(HashSet<string> set, string argsJson)
    {
        if (set.Contains("shell") || set.Contains("*") || set.Contains("all")) return false;
        if (!set.Contains("display")) return false;
        string cmd = "";
        try { cmd = System.Text.Json.Nodes.JsonNode.Parse(argsJson)?.AsObject()?["command"]?.GetValue<string>() ?? ""; }
        catch { return false; }
        cmd = cmd.Trim();
        if (!cmd.StartsWith("echo ", StringComparison.Ordinal) && cmd != "echo") return false;
        string rest = cmd == "echo" ? "" : cmd[5..];
        return !rest.Contains('|') && !rest.Contains('>') && !rest.Contains('<') && !rest.Contains(';') && !rest.Contains("&&");
    }

    // Value objects do dominio: catalogo identico ao opencode (fonte unica).

    public sealed record ToolDefinition(
        string Name,
        string Description,
        string ParametersJson);

    private static JsonObject BuildTool(ToolDefinition def)
    {
        var pars = System.Text.Json.Nodes.JsonNode.Parse(def.ParametersJson)?.AsObject()
            ?? new System.Text.Json.Nodes.JsonObject { ["type"] = "object" };
        return new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "function",
            ["function"] = new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["parameters"] = pars,
            },
        };
    }

    // Catalogo tipado (fonte unica): GetTools projeta p/ o formato chat.
    public static IReadOnlyList<ToolDefinition> Catalog { get; } = BuildCatalog();

    // Catalogo identico ao opencode (source: trafego medido na sonda).
    // Controle de uso no nosso lado: Allowed (CSV) + handlers.
    private static ToolDefinition[] BuildCatalog() => new ToolDefinition[]
    {
        new("edit", @"Edit the contents of a file by finding and replacing exact text. When editing text from Read output, preserve the exact indentation (tabs or spaces) and omit the line-number prefix, such as `1: `. Never include the prefix in oldString or newString. The edit fails if oldString is not found. By default, oldString must identify a UNIQUE location. Multiple matches FAIL unless replaceAll is true. Add more surrounding context to disambiguate, or set replaceAll to true to replace every occurrence. Use replaceAll when the change should apply to every occurrence, such as renaming a variable.", @"{""type"": ""object"", ""properties"": {""path"": {""type"": ""string"", ""description"": ""File to edit""}, ""oldString"": {""type"": ""string"", ""description"": ""Exact text to find and replace""}, ""newString"": {""type"": ""string"", ""description"": ""Text to replace oldString with (must differ from oldString)""}, ""replaceAll"": {""type"": ""boolean"", ""description"": ""Whether to replace every occurrence of oldString. When false, oldString must match exactly once. Defaults to false.""}}, ""required"": [""path"", ""oldString"", ""newString""], ""additionalProperties"": false}"),
        new("glob", @"Search file paths using a glob pattern (examples: ""**/*.ts"", ""src/**/*.tsx"").", @"{""type"": ""object"", ""properties"": {""pattern"": {""type"": ""string"", ""description"": ""Glob pattern to match files against""}, ""path"": {""type"": ""string"", ""description"": ""Directory to search. Defaults to the current working directory.""}, ""hidden"": {""type"": ""boolean"", ""description"": ""Include hidden files and directories (default: false).""}, ""limit"": {""type"": ""integer"", ""exclusiveMinimum"": 0, ""description"": ""Maximum number of matching files to return (default: 100)""}}, ""required"": [""pattern""], ""additionalProperties"": false}"),
        new("grep", @"Search file contents using ripgrep's regular expression syntax or literal text matching. Use it to locate specific code, symbols, or text patterns, and narrow searches with `path` or `include`. Returns matching file paths, line numbers, and line previews.", @"{""type"": ""object"", ""properties"": {""pattern"": {""type"": ""string"", ""minLength"": 1, ""description"": ""Regular expression or literal text to match in file contents.""}, ""path"": {""type"": ""string"", ""description"": ""File or directory to search. Defaults to the current working directory.""}, ""include"": {""type"": ""string"", ""description"": ""Glob pattern to filter files (for example, \""*.js\"" or \""*.{ts,tsx}\"")""}, ""literal"": {""type"": ""boolean"", ""description"": ""Treat `pattern` as exact text instead of a regular expression (default: false).""}, ""caseSensitive"": {""type"": ""boolean"", ""description"": ""Use case-sensitive matching (default: true).""}, ""limit"": {""type"": ""integer"", ""exclusiveMinimum"": 0, ""description"": ""Maximum number of matching lines to return (default: 100)""}}, ""required"": [""pattern""], ""additionalProperties"": false}"),
        new("question", @"Use this tool when you need to ask the user questions during execution. This allows you to:
1. Gather user preferences or requirements
2. Clarify ambiguous instructions
3. Get decisions on implementation choices as you work
4. Offer choices to the user about what direction to take.

Usage notes:
- A ""Type your own answer"" option is added automatically; don't include a separate option for free form answers
- Set `multiple: true` to allow selecting more than one option
- If you recommend a specific option, make that the first option in the list and add ""(Recommended)"" at the end of the label", @"{""type"": ""object"", ""properties"": {""questions"": {""type"": ""array"", ""items"": {""type"": ""object"", ""properties"": {""question"": {""type"": ""string"", ""description"": ""Complete question""}, ""header"": {""type"": ""string"", ""description"": ""Very short label (max 30 chars)""}, ""options"": {""type"": ""array"", ""items"": {""type"": ""object"", ""properties"": {""label"": {""type"": ""string"", ""description"": ""Display text (1-5 words, concise)""}, ""description"": {""type"": ""string"", ""description"": ""Explanation of choice""}}, ""required"": [""label"", ""description""], ""additionalProperties"": false}, ""description"": ""Available choices""}, ""multiple"": {""type"": ""boolean""}}, ""required"": [""question"", ""header"", ""options""], ""additionalProperties"": false}, ""minItems"": 1, ""description"": ""Questions to ask""}}, ""required"": [""questions""], ""additionalProperties"": false}"),
        new("read", @"Read the contents of a file or directory. Supports text files, images, and PDFs. Images and PDFs are presented directly to the model. Each text line is prefixed by its 1-based line number as <line>: <content>. The prefix is for reference and is not part of the file content. Directory entries are returned one per line. Use offset and limit to read large files or directories in sections. Prefer one larger read over many small slices, and use grep to find specific content in large files.", @"{""type"": ""object"", ""properties"": {""path"": {""type"": ""string"", ""description"": ""File or directory to read""}, ""offset"": {""type"": ""integer"", ""minimum"": 0, ""description"": ""The line or directory entry to start reading from (1-based)""}, ""limit"": {""type"": ""integer"", ""minimum"": 0, ""description"": ""The maximum number of lines or directory entries to read (defaults to and capped at 2000)""}}, ""required"": [""path""], ""additionalProperties"": false}"),
        new("shell", @"Execute a shell command and return its output. Commands run on Linux using bash. Quote file paths containing spaces or special characters. Prefer dedicated tools over shell commands when possible. When output is large, the full result is saved to a file and a truncated preview is returned. Rely on automatic truncation unless filtering the output is more useful. Commands accept an optional timeout, background commands have no timeout by default. Background commands return immediately, and you will be notified when they complete.", @"{""type"": ""object"", ""properties"": {""command"": {""type"": ""string"", ""description"": ""Shell command string to execute""}, ""workdir"": {""type"": ""string"", ""description"": ""Working directory to execute the command in. Defaults to the current working directory. When possible, avoid changing directories in the command and set the working directory here instead.""}, ""timeout"": {""type"": ""integer"", ""minimum"": 0, ""description"": ""Timeout in milliseconds. Set to 0 to disable the timeout. Defaults to 120000 for foreground commands. Background commands have no timeout by default.""}, ""background"": {""type"": ""boolean"", ""description"": ""Run the command in the background and return immediately (useful for dev servers and long-running builds). You do not need to use '&' at the end of the command when using this parameter. You will be notified when it completes. DO NOT poll for completion.""}}, ""required"": [""command""], ""additionalProperties"": false}"),
        new("skill", @"Load a specialized skill's instructions and resources into the current conversation when the task at hand matches its description.

The skill ID must match an available skill or a skill explicitly referenced by the user.", @"{""type"": ""object"", ""properties"": {""id"": {""type"": ""string"", ""description"": ""The ID of an available skill or a skill explicitly referenced by the user""}}, ""required"": [""id""], ""additionalProperties"": false}"),
        new("subagent", @"Spawns an agent in a child session to work on the specified task.
The output includes a sessionID you can pass back later to continue that specific conversation with the subagent.
New child sessions start with fresh context, so include all relevant context and instructions when you don't pass a sessionID.
Foreground (default) runs the subagent to completion and returns its final response.
Background mode (background=true) launches it asynchronously and returns immediately; you are notified when it finishes.
Use background only for independent work that can run while you continue elsewhere.

Available subagents:
- build-exec: Subagente executor Build 9B chamado pelo Orch
- explore: Fast agent specialized for exploring codebases. Use this when you need to quickly find files by patterns (eg. ""src/components/**/*.tsx""), search code for keywords (eg. ""API endpoints""), or answer questions about the codebase (eg. ""how do API endpoints work?""). When calling this agent, specify the desired thoroughness level: ""quick"" for basic searches, ""medium"" for moderate exploration, or ""very thorough"" for comprehensive analysis across multiple locations and naming conventions.
- general: General-purpose agent for researching complex questions and executing multi-step tasks. Use this agent to execute multiple units of work in parallel.
- scpt: Subagente scripter 9B chamado pelo Orch (faixa azul)", @"{""type"": ""object"", ""properties"": {""agent"": {""type"": ""string"", ""description"": ""The type of specialized agent to use for this task. If the user asks for a subagent by a name that is not one of the available subagents, they most likely mean a model: pick a suitable agent and pass the name through the model parameter instead.""}, ""description"": {""type"": ""string"", ""description"": ""A short 3-5 word label for the task, displayed to the user""}, ""prompt"": {""type"": ""string"", ""description"": ""The task for the subagent to perform""}, ""model"": {""type"": ""string"", ""description"": ""NEVER set this unless the user explicitly asks for a particular model or variant. The value is written as \""providerID/modelID\"", or \""providerID/modelID#variant\"" to include a variant. Do not guess the ID: look the model up with the models tool, filtering to your own provider first.""}, ""sessionID"": {""type"": ""string"", ""pattern"": ""^ses"", ""description"": ""Continue a specific previous subagent conversation by passing its sessionID. Calls without a sessionID start a new conversation.""}, ""background"": {""type"": ""boolean"", ""description"": ""Run the subagent in the background and return immediately. You will be notified when it completes. DO NOT sleep, poll, or proactively check on its progress.""}}, ""required"": [""agent"", ""description"", ""prompt""], ""additionalProperties"": false}"),
        new("webfetch", @"Fetch content from an HTTP or HTTPS URL and return it as text, markdown, or HTML. Markdown is the default.

Use a more targeted tool when one is available. This tool is read-only. Large text results may be replaced with a preview while the complete output is retained in managed storage.", @"{""type"": ""object"", ""properties"": {""url"": {""type"": ""string"", ""description"": ""The HTTP or HTTPS URL to fetch content from""}, ""format"": {""type"": ""string"", ""enum"": [""text"", ""markdown"", ""html""], ""description"": ""The format to return the content in. Defaults to markdown.""}, ""timeout"": {""type"": ""number"", ""exclusiveMinimum"": 0, ""maximum"": 120, ""description"": ""Optional timeout in seconds (maximum: 120)""}}, ""required"": [""url""], ""additionalProperties"": false}"),
        new("websearch", @"Search the web using the user's selected search integration. Use this for current information beyond knowledge cutoff.

The current year is 2026. Use this year when searching for recent information or current events.", @"{""type"": ""object"", ""properties"": {""query"": {""type"": ""string"", ""description"": ""Websearch query""}}, ""required"": [""query""], ""additionalProperties"": false}"),
        new("write", @"Writes a file to the local filesystem, overwriting if one exists.

Missing parent directories are created automatically.

Use this tool to create new files or overwrite existing files. For partial changes, use the edit tool instead.", @"{""type"": ""object"", ""properties"": {""path"": {""type"": ""string"", ""description"": ""Path to the file to write to""}, ""content"": {""type"": ""string"", ""description"": ""Content to write to the file""}}, ""required"": [""path"", ""content""], ""additionalProperties"": false}"),
        new("execute", @"Run JavaScript in a confined Code Mode runtime to script tool calls and HTTP requests and compose their results.
`fetch` is available for HTTP requests. Imports, direct filesystem access, and timers are unavailable; all other external access goes through `tools`.
Within `{ code }`, the only callable tools are those explicitly listed in the Code Mode catalog instructions or returned by the `search` function. Inside `{ code }`, ignore tools shown outside the Code Mode catalog. They are not available in the Code Mode runtime.
Call tools through `tools` using only exact paths and signatures from the catalog. Do not infer or normalize tool names; preserve bracket notation such as `tools.<namespace>[""tool-name""](input)`.
Prefer an explicit `return`; if omitted, the final top-level expression becomes the result.
Await every call whose completion matters; pending calls are interrupted when execution ends. Run independent calls concurrently with `Promise.all`.", @"{""type"": ""object"", ""properties"": {""code"": {""type"": ""string""}}, ""required"": [""code""], ""additionalProperties"": false}"),
    };

    // task tool - REMOVIDO: sem executor implementado, não anunciar ao modelo.
    // (Reintroduzir aqui + handler dedicado quando houver subagentes.)
}
