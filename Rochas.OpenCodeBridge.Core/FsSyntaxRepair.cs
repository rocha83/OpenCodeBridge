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

        // FS0039 `open System.Math`: `Math` e' TIPO, nao namespace. O modelo copia
        // o literal da dica, entao o CERTO mostra a chamada qualificada (nunca `open`).
        if (compilerError.Contains("FS0039", StringComparison.Ordinal)
            && Regex.IsMatch(source, @"\bopen\s+System\.Math\b"))
            return "ERRADO: open System.Math\n" +
                   "CERTO: open System   (Math e' TIPO, nao namespace: chame System.Math.Sin/Sqrt/PI qualificado)";

        // FS0001 `List.*` recebendo `[| ... |]`: o compilador diz lista esperada e
        // array recebido. A dica nomeia a lista (`[...]`) e o `Array.*` correto.
        if (compilerError.Contains("FS0001", StringComparison.Ordinal)
            && compilerError.Contains("list", StringComparison.OrdinalIgnoreCase)
            && compilerError.Contains("array", StringComparison.OrdinalIgnoreCase))
            return "CERTO: `[ a; b ]` (lista) com `List.sum/List.map/List.length`; " +
                   "`[| a; b |]` e' ARRAY: use `Array.sum/Array.map/Array.length`";

        // So' os codigos que este mapa cobre: erro alheio nao recebe conserto de
        // comprehension (evita "corrigir" a linha errada do fonte).
        if (!new[] { "FS0010", "FS0001", "FS0020", "FS0751" }
                .Any(code => compilerError.Contains(code, StringComparison.Ordinal)))
            return "";

        // Comprehension de lista: `|` de outra linguagem, `do` sem `yield` e
        // `do`+`->` juntos — as tres formas medidas no L3 de 2026-10-29.
        foreach (Match m in Regex.Matches(source, ComprehensionRx))
        {
            string fix = FixComprehension(m.Groups["body"].Value);
            if (fix.Length > 0)
                return $"ERRADO: {m.Value}\nCERTO: {fix}";
        }
        return "";
    }

    /// <summary>Comprehension de lista com <c>for</c> dentro (uma por linha).</summary>
    const string ComprehensionRx = @"\[(?<body>[^\[\]\r\n]*\bfor\b[^\[\]\r\n]*)\]";

    /// <summary>
    /// Corpo de comprehension errada (-&gt; forma correta) ou vazio. Contrato: o
    /// CERTO nunca mistura <c>do</c> com <c>-&gt;</c> nem devolve lista <c>unit</c> —
    /// o modelo copia o literal da dica (DRIVER-FIXES #13 e #20).
    /// </summary>
    /// <param name="body">Texto entre colchetes.</param>
    /// <returns>Comprehension valida em F#, ou "" quando nao ha mapa conhecido.</returns>
    static string FixComprehension(string body)
    {
        if (body.Contains('\n')) return "";   // corpo multilinha: nao adivinha
        // Guarda 1: `yield` em qualquer ponto ja' declara o elemento — forma valida,
        // nao se toca (senao `[for i in r do if c then yield x]` viraria arrow+yield).
        if (Regex.IsMatch(body, @"\byield\b")) return "";
        // Guarda 2: so' o que e' conhecidamente suspeito — `|` de outra linguagem ou
        // `do` dentro de lista. Forma apenas com `->` e' F# valido: nao se toca.
        if (!body.Contains('|') && !Regex.IsMatch(body, @"\sdo\s")) return "";

        // (1) `[expr | for v in range]` — comprehension de Python/Haskell/JS.
        var pipe = Regex.Match(body, @"^(?<expr>.+?)\s*\|\s*for\s+(?<v>\w+)\s+in\s+(?<range>.+)$");
        if (pipe.Success)
        {
            string expr = pipe.Groups["expr"].Value.Trim();
            var (range, cond) = SplitDo(pipe.Groups["range"].Value.Trim());
            string r = StepRange(range, cond) ?? range;
            return $"[for {pipe.Groups["v"].Value} in {r} -> {expr}]";
        }

        // (2) `[for v in range do <tail>]` — `do` dentro de LISTA.
        var doForm = Regex.Match(body, @"^for\s+(?<v>\w+)\s+in\s+(?<range>.+?)\s+do\s+(?<tail>.+)$");
        if (!doForm.Success) return "";
        string v = doForm.Groups["v"].Value;
        string rng = doForm.Groups["range"].Value.Trim();
        string tail = doForm.Groups["tail"].Value.Trim();
        if (tail.StartsWith("yield ", StringComparison.Ordinal)) return "";   // ja' e' F# valido

        // `do cond -> expr`: `do` e `->` juntos. A condicao tem que vir antes do
        // arrow e sem parenteses (lambda `fun x -> ...` nao casa) — assim o `do`
        // desaparece e o resto vira forma valida.
        var arrow = Regex.Match(tail, @"^(?<cond>[^()\[\]]*?)->\s*(?<expr>.+)$");
        if (arrow.Success)
        {
            string cond = arrow.Groups["cond"].Value.Trim();
            string expr = arrow.Groups["expr"].Value.Trim();
            if (cond.Length == 0) return $"[for {v} in {rng} -> {expr}]";
            return Filtered(v, rng, cond, expr);
        }

        // `do expr` puro (`if c then e1 else e2` inclusive): `->` resolve os dois.
        return $"[for {v} in {rng} -> {tail}]";
    }

    /// <summary>Condicao do comprehension: vira passo do range (paridade) ou `do if ... then yield`.</summary>
    /// <param name="v">Variavel do `for`.</param>
    /// <param name="range">Range original.</param>
    /// <param name="cond">Condicao (com ou sem `if`).</param>
    /// <param name="expr">Expressao do elemento.</param>
    /// <returns>Comprehension valida.</returns>
    static string Filtered(string v, string range, string cond, string expr)
    {
        string? step = StepRange(range, cond);
        if (step is not null) return $"[for {v} in {step} -> {expr}]";
        string c = cond.StartsWith("if ", StringComparison.Ordinal) ? cond[3..].Trim() : cond;
        // Residuo do proprio indice (`... do i`): nao e' teste booleano nem filtro,
        // entao sai. Qualquer outra condicao (ex.: `isAdult x`) e' FILTRO legitimo
        // e vira `do if ... then yield` — nunca descartar condicao em silencio.
        if (Regex.IsMatch(c, @"^\w+$")) return $"[for {v} in {range} -> {expr}]";
        return $"[for {v} in {range} do if {c} then yield {expr}]";
    }

    /// <summary>
    /// Filtro de paridade sobre a variavel vira passo do range. `i % 2 = 1`,
    /// `i % 2 &lt;&gt; 0` e `i =% 2` (digito perdido) = impar; `i % 2 = 0` e
    /// `i % 2 &lt;&gt; 1` = par — com o inicio ajustado quando e' literal inteiro.
    /// </summary>
    /// <param name="range">Range sem a condicao (`1 .. n - 1`).</param>
    /// <param name="cond">Condicao (com ou sem `if`).</param>
    /// <returns>Range com passo, ou null quando nao e' paridade conhecida.</returns>
    static string? StepRange(string range, string? cond)
    {
        if (string.IsNullOrWhiteSpace(cond)) return null;
        string c = cond.Trim();
        if (c.StartsWith("if ", StringComparison.Ordinal)) c = c[3..].Trim();

        var m = Regex.Match(c, @"^(?<v>\w+)\s*(?<op>%|=%)\s*2\s*(?:(?<cmp>=|<>)\s*(?<k>\d+))?$");
        if (!m.Success) return null;
        bool even = m.Groups["cmp"].Success
            ? (m.Groups["cmp"].Value == "=" ? m.Groups["k"].Value == "0" : m.Groups["k"].Value == "1")
            : false;   // `i =% 2` sem comparacao: leitura de "resta 1" = impar

        if (Regex.Matches(range, @"\.\.").Count >= 2) return null;   // ja' tem passo
        var parts = Regex.Match(range, @"^(?<a>\S+)\s*\.\.\s*(?<b>.+)$");
        if (!parts.Success) return null;
        string a = parts.Groups["a"].Value.Trim();
        string b = parts.Groups["b"].Value.Trim();
        if (int.TryParse(a, out int ai) && (ai % 2 == 0) != even) a = (ai + 1).ToString();
        return $"{a} .. 2 .. {b}";
    }

    /// <summary>Separa `range do cond` (paridade no fim do range) e normaliza `step`.</summary>
    /// <param name="range">Trecho do range, possivelmente com `do cond`.</param>
    /// <returns>Range sem condicao + condicao (null quando nao havia).</returns>
    static (string Range, string? Cond) SplitDo(string range)
    {
        var m = Regex.Match(range, @"^(?<r>.+?)\s+do\s+(?<c>.+)$");
        return m.Success
            ? (StepToRange(m.Groups["r"].Value.Trim()), m.Groups["c"].Value.Trim())
            : (StepToRange(range), null);
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
