using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Reparo de sintaxe F# no retry: dado o erro do compilador e o fonte atual, devolve
/// o "ERRADO -&gt; CERTO" do construto que o modelo repete (comprehension de outra
/// linguagem, "step" no range). Generico de proposito: nao conhece o algoritmo do
/// pedido, so a forma da linguagem. Unico lugar dessa mecanica — o teste consome.
/// </summary>
public static class FsSyntaxRepair
{
    /// <summary>
    /// Sugestao concreta para o erro. Vazio quando nao ha mapa conhecido — nesse
    /// caso o chamador mantem so a linha do compilador (sem inventar correcao).
    /// </summary>
    /// <param name="source">Fonte F# atual (texto completo).</param>
    /// <param name="compilerError">Linha de erro do compilador.</param>
    /// <returns>"ERRADO: ...\nCERTO: ..." ou string vazia.</returns>
    public static string Suggest(string source, string compilerError)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(compilerError)) return "";

        // FS0010 com '|' dentro de lista: comprehension de Python/JS.
        if (compilerError.Contains("FS0010", StringComparison.Ordinal)
            && compilerError.Contains("'|'", StringComparison.Ordinal))
        {
            var m = Regex.Match(source,
                @"\[\s*(?<expr>[^\[\]|]+?)\s*\|\s*for\s+(?<v>\w+)\s+in\s+(?<range>[^\[\]|]+?)\s*\]");
            if (m.Success)
            {
                string expr = m.Groups["expr"].Value.Trim();
                string range = StepToRange(m.Groups["range"].Value.Trim());
                return $"ERRADO: {m.Value}\nCERTO: [for {m.Groups["v"].Value} in {range} -> {expr}]";
            }
        }
        return "";
    }

    /// <summary>"1 .. n-1 step 2" -&gt; "1 .. 2 .. n-1" (passo vai no MEIO).</summary>
    /// <param name="range">Trecho do range.</param>
    /// <returns>Range na forma F#.</returns>
    static string StepToRange(string range)
    {
        var s = Regex.Match(range, @"^(?<a>\S+)\s*\.\.\s*(?<b>.+?)\s+step\s+(?<st>\d+)$");
        return s.Success ? $"{s.Groups["a"].Value} .. {s.Groups["st"].Value} .. {s.Groups["b"].Value}" : range;
    }
}
