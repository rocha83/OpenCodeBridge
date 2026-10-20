using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.AI.Manager.Application.Helpers;

public static class ReasoningSanitizer
{
    /// <summary>Remove itens de reasoning (type=reasoning) de uma resposta chat, retornando o JSON limpo.</summary>
    public static string Sanitize(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null) return json;
            var choices = root["choices"]?.AsArray();
            if (choices is null) return json;
            foreach (var c in choices)
            {
                var msg = c?["message"]?.AsObject();
                if (msg is null) continue;
                // remove reasoning_content field
                msg.Remove("reasoning_content");
                msg.Remove("reasoning");
                // remove reasoning items from content array if present
                if (msg["content"] is JsonArray arr)
                {
                    var filtered = new JsonArray();
                    foreach (var item in arr)
                    {
                        var t = item?["type"]?.GetValue<string>();
                        if (t == "reasoning") continue;
                        filtered.Add(item?.DeepClone());
                    }
                    msg["content"] = filtered;
                }
            }
            return root.ToJsonString();
        }
        catch { return json; }
    }
}