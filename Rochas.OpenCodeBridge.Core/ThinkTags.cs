using System.Text;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Higiene de conteudo: marcadores de pensamento (<c>&lt;think&gt;</c>,
/// <c>&lt;thinking&gt;</c>) vazados no content voltam para o canal de thinking.
/// Regra de protocolo, usada por qualquer consumidor (Web, bridge, testes).
/// </summary>
public static class ThinkTags
{
    /// <summary>Remove as marcas do texto e as acumula em <paramref name="thinking"/>.</summary>
    /// <param name="content">Texto possivelmente contendo as marcas.</param>
    /// <param name="thinking">Destino do raciocinio extraido.</param>
    /// <returns>Texto limpo, sem as marcas.</returns>
    public static string Strip(string? content, StringBuilder thinking)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";
        string s = content;
        if (s.Contains("<thinking>", StringComparison.Ordinal))
        {
            s = Regex.Replace(s, "<thinking>([\\s\\S]*?)</thinking>",
                m => { thinking.Append(m.Groups[1].Value); return ""; });
            int oi = s.IndexOf("<thinking>", StringComparison.Ordinal);
            if (oi >= 0) { thinking.Append(s[(oi + 10)..]); s = s[..oi]; }
        }
        if (s.Contains("<think>", StringComparison.Ordinal))
        {
            s = Regex.Replace(s, "<think>([\\s\\S]*?)</think>",
                m => { thinking.Append(m.Groups[1].Value); return ""; });
            int oi = s.IndexOf("<think>", StringComparison.Ordinal);
            if (oi >= 0) { thinking.Append(s[(oi + 7)..]); s = s[..oi]; }
        }
        return s.Replace("</think>", "", StringComparison.Ordinal);
    }
}
