using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Catálogo canônico das tools de sistema que a bridge expõe em
/// <c>/api/tools/execute</c>. É a fonte única do "que o modelo pode chamar":
/// o cliente (Web UI, teste, CLI) só repassa este array — não inventa tool nem
/// reescreve schema.
/// </summary>
/// <remarks>
/// Dois shapes, os mesmos do protocolo:
/// <list type="bullet">
/// <item><see cref="Chat"/> — <c>chat/completions</c>: <c>{type:function, function:{name,description,parameters}}</c>.</item>
/// <item><see cref="Responses"/> — <c>/v1/responses</c>: <c>{type:function, name, description, parameters}</c>.</item>
/// </list>
/// A forma flat não é cosmética: mandar o shape chat no endpoint Responses faz a
/// bridge não reconhecer a call de volta e o turno volta vazio.
/// </remarks>
public static class ToolCatalog
{
    /// <summary>Tools no shape <c>chat/completions</c>.</summary>
    /// <returns>Array de tools (8).</returns>
    public static JsonArray Chat()
    {
        var tools = new JsonArray
        {
            Fn("shell", "Executa um comando de console. Um comando por chamada; sem heredoc.", new JsonObject
            {
                ["command"] = new JsonObject { ["type"] = "string" }
            }, "command"),
            Fn("write", "Cria/sobrescreve um arquivo de texto.", new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string" },
                ["content"] = new JsonObject { ["type"] = "string" }
            }, "path", "content"),
            Fn("read", "Le um arquivo de texto.", new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string" },
                ["offset"] = new JsonObject { ["type"] = "integer" },
                ["limit"] = new JsonObject { ["type"] = "integer" }
            }, "path"),
            Fn("edit", "Substitui um trecho exato de um arquivo.", new JsonObject
            {
                ["path"] = new JsonObject { ["type"] = "string" },
                ["oldString"] = new JsonObject { ["type"] = "string" },
                ["newString"] = new JsonObject { ["type"] = "string" },
                ["replaceAll"] = new JsonObject { ["type"] = "boolean" }
            }, "path", "oldString", "newString"),
            Fn("glob", "Lista arquivos por padrao. Nao aceita alternancia com |.", new JsonObject
            {
                ["pattern"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" }
            }, "pattern"),
            Fn("grep", "Busca expressao regular no conteudo dos arquivos.", new JsonObject
            {
                ["pattern"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" },
                ["include"] = new JsonObject { ["type"] = "string" }
            }, "pattern"),
            Fn("webfetch", "Busca conteudo de uma URL http(s) e retorna como texto (text/markdown/html); somente leitura.", new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string" },
                ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("text", "markdown", "html") },
                ["timeout"] = new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0, ["maximum"] = 120 }
            }, "url"),
            Fn("git", "Executa um comando git no repositorio. Leitura para todos; init/add/commit/config so para build/exec.", new JsonObject
            {
                ["command"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" }
            }, "command")
        };
        return ToolHelp.Apply(tools);
    }

    /// <summary>Tools no shape <c>/v1/responses</c> (a partir de <see cref="Chat"/>).</summary>
    /// <returns>Array de tools no formato flat.</returns>
    public static JsonArray Responses()
    {
        var arr = new JsonArray();
        foreach (var t in Chat())
        {
            if (t?["function"] is JsonObject fn)
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = fn["name"]?.DeepClone(),
                    ["description"] = fn["description"]?.DeepClone(),
                    ["parameters"] = fn["parameters"]?.DeepClone()
                });
            else if (t is not null) arr.Add(t.DeepClone());
        }
        return arr;
    }

    static JsonObject Fn(string name, string description, JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
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
                    ["required"] = req
                }
            }
        };
    }
}
