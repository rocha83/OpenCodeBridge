using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Tool definitions for the bridge (chat/completions format).
// Enxutas de propósito: cada request carrega o array (~400 tokens); descrições
// curtas bastam para o modelo acertar as chamadas. Retorna instância nova por
// chamada (JsonNode tem um só pai — cache estático quebraria o loop).
public static class ToolDefinitions
{
    // mode "plan": só leitura e navegação (sem escrita nem execução).
    public static JsonArray GetTools(string? mode = null)
    {
        bool plan = mode == "plan";
        var tools = new JsonArray();
        if (!plan)
            tools.Add(BuildTool("shell", "Executa comando shell no workspace. Retorna stdout/stderr.",
                new[] { BuildParam("command", "string", "Comando (ex.: 'ls -la').", true) }));
        tools.Add(BuildTool("read", "Lê arquivo de texto do workspace.",
            new[] { BuildParam("path", "string", "Caminho do arquivo.", true), BuildParam("offset", "integer", "Linha inicial (0-based).", false), BuildParam("limit", "integer", "Máx. linhas (padrão 2000).", false) }));
        if (!plan)
            tools.Add(BuildTool("write", "Escreve arquivo (cria diretórios).",
                new[] { BuildParam("path", "string", "Caminho do arquivo.", true), BuildParam("content", "string", "Conteúdo.", true) }));
        if (!plan)
            tools.Add(BuildTool("edit", "Substitui texto exato no arquivo.",
                new[] { BuildParam("path", "string", "Caminho do arquivo.", true), BuildParam("oldString", "string", "Texto exato a achar.", true), BuildParam("newString", "string", "Substituto.", true), BuildParam("replaceAll", "boolean", "Trocar todas (padrão false).", false) }));
        tools.Add(BuildTool("grep", "Busca regex nos arquivos. Retorna arquivo:linha:texto.",
            new[] { BuildParam("pattern", "string", "Regex ou texto.", true), BuildParam("path", "string", "Diretório (padrão raiz).", false), BuildParam("include", "string", "Glob de filtro (ex.: '*.cs').", false) }));
        tools.Add(BuildTool("glob", "Lista caminhos por padrão glob.",
            new[] { BuildParam("pattern", "string", "Glob (ex.: '**/*.cs').", true), BuildParam("path", "string", "Diretório (padrão raiz).", false) }));
        return tools;
    }

    private sealed record ToolParam(string Name, string Type, string Description, bool Required);

    private static ToolParam BuildParam(string name, string type, string description, bool required) => new(name, type, description, required);

    private static JsonObject BuildTool(string name, string description, ToolParam[] pars)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in pars)
        {
            props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = required,
                },
            },
        };
    }

    // task tool - REMOVIDO: sem executor implementado, não anunciar ao modelo.
    // (Reintroduzir aqui + handler dedicado quando houver subagentes.)
}
