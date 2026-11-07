using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

/// <summary>
/// Formatos de tool call em TEXTO da família Qwen2.5-Coder / hermes (o modelo
/// ecoa o wrapper do próprio template, em vez de emitir <c>tool_calls</c>):
/// <list type="number">
/// <item><c>&lt;tool_call&gt;{"name":…,"arguments":{…}}&lt;/tool_call&gt;</c> — hermes;</item>
/// <item><c>&lt;tools&gt;{…}&lt;/tools&gt;</c> — wrapper plural do coder;</item>
/// <item><c>&lt;function&gt;{…}&lt;/function&gt;</c> — variante com <c>tool_choice: required</c>.</item>
/// </list>
/// A validação (allowlist/catálogo, nome canônico, args) fica no chamador:
/// aqui só se varre o texto e se devolve o envelope JSON de cada bloco.
/// </summary>
public static class QwenToolCalls
{
    /// <summary>Hermes: <c>&lt;tool_call&gt;{json}&lt;/tool_call&gt;</c>.</summary>
    public static readonly Regex ReHermes = new(
        @"<tool_call>\s*(\{.*?\})\s*</tool_call>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Wrapper plural do coder: <c>&lt;tools&gt;{json}&lt;/tools&gt;</c>.</summary>
    public static readonly Regex RePlural = new(
        @"<tools>\s*(\{.*?\})\s*</tools>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Com choice required: <c>&lt;function&gt;{json}&lt;/function&gt;</c>.</summary>
    public static readonly Regex ReFunction = new(
        @"<function>\s*(\{.*?\})\s*</function>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Varre os três formatos na ordem hermes → plural → function e devolve o
    /// texto sem os blocos que o chamador aceitou.
    /// </summary>
    /// <param name="text">Content do assistente.</param>
    /// <param name="tryEnvelope">Recebe o envelope JSON; true = virou call.</param>
    /// <param name="hasRoom">False quando o teto de calls do turno estourou.</param>
    /// <returns>Texto com os blocos consumidos removidos.</returns>
    public static string Extract(string text, Func<string, bool> tryEnvelope, Func<bool> hasRoom)
    {
        text = Scan(text, ReHermes, tryEnvelope, hasRoom);
        text = Scan(text, RePlural, tryEnvelope, hasRoom);
        text = Scan(text, ReFunction, tryEnvelope, hasRoom);
        return text;
    }

    /// <summary>True quando o texto traz qualquer um dos marcadores Qwen/hermes.</summary>
    /// <param name="text">Texto.</param>
    /// <returns>Marcador presente.</returns>
    public static bool HasCall(string? text)
        => !string.IsNullOrEmpty(text) &&
           (ReHermes.IsMatch(text) || RePlural.IsMatch(text) || ReFunction.IsMatch(text));

    static string Scan(string text, Regex re, Func<string, bool> tryEnvelope, Func<bool> hasRoom)
    {
        foreach (Match m in re.Matches(text))
        {
            if (!hasRoom()) break;
            if (tryEnvelope(m.Groups[1].Value)) text = text.Replace(m.Value, "\n");
        }
        return text;
    }
}
