using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Formato ÚNICO dos avisos que a bridge injeta no histórico como instrução
/// (<c>BRIDGE (escopo): texto</c>). Existe para o prefixo, a tag e a regra de
/// "não empilhar cópia" viverem em um lugar só — antes o literal
/// <c>"BRIDGE ("</c> aparecia em três pontos (chamada do salvage, predicado do
/// salvage e marcador de outro aviso) e cada um deduplicava do seu jeito.
/// </summary>
public static class BridgeNotice
{
    /// <summary>Prefixo comum: é o que identifica "isto é instrução da bridge".</summary>
    public const string Prefix = "BRIDGE (";

    /// <summary>Tag vazia usa o escopo genérico (nunca sai "BRIDGE ()").</summary>
    public const string DefaultScope = "tool";

    /// <summary>Tag pronta para o escopo (ex.: <c>edit, write</c>).</summary>
    /// <param name="scope">Escopo: nome de tool ou rótulo curto.</param>
    /// <returns>Prefixo + escopo + fechamento.</returns>
    public static string Tag(object? scope)
    {
        string s = scope?.ToString() ?? "";
        return Prefix + (s.Length > 0 ? s : DefaultScope) + "): ";
    }

    /// <summary>Aviso completo (tag + corpo).</summary>
    /// <param name="scope">Escopo: nome de tool ou rótulo curto.</param>
    /// <param name="body">Texto da instrução.</param>
    /// <returns>Texto do aviso.</returns>
    public static string Notice(object? scope, string body) => Tag(scope) + body;

    /// <summary>O conteúdo é instrução da bridge?</summary>
    /// <param name="content">Conteúdo de mensagem.</param>
    /// <returns>True quando traz o prefixo.</returns>
    public static bool Is(string? content) =>
        !string.IsNullOrEmpty(content) && content.Contains(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Anexa o aviso como mensagem de usuário, uma vez só: se a última mensagem
    /// do histórico já é esse mesmo texto, não acrescenta cópia.
    /// </summary>
    /// <param name="history">Histórico chat (mutado só quando anexa).</param>
    /// <param name="notice">Texto do aviso (ver <see cref="Notice"/>).</param>
    /// <returns>True quando anexou.</returns>
    public static bool Append(JsonArray history, string notice)
    {
        if (history.Count > 0 && history[^1] is JsonObject last
            && (last["content"]?.GetValue<string>() ?? "").Contains(notice, StringComparison.Ordinal))
            return false;
        history.Add(new JsonObject { ["role"] = "user", ["content"] = notice });
        return true;
    }
}
