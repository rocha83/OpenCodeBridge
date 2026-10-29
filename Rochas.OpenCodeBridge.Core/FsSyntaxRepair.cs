using System.Collections.Generic;
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
        // Despacha pelo PRIMEIRO erro: a saida do `dotnet` traz varios, e decidir pelo
        // blob fazia a dica de array/lista responder a um FS0039 (medido 2026-10-29).
        string code = FirstCode(compilerError);

        // FS3563 identificador invalido: crase SIMPLES nao existe em F#. `mod` e'
        // operador puro; escrever `a `mod` b` gera FS3563 e o modelo repete o mesmo
        // edit (medido no isolado de 2026-10-29). O CERTO sai sem crase.
        if (code == "FS3563" || compilerError.Contains("not a valid identifier", StringComparison.Ordinal))
        {
            var bt = Regex.Match(source, @"`(?<w>\w+)`");
            if (bt.Success)
                return $"ERRADO: `{bt.Groups["w"].Value}`\n" +
                       $"CERTO: {bt.Groups["w"].Value}   (crase simples nao existe em F#: escreva o nome/operador sem crase)";
        }

        // FS0010 `let`/`use` inesperado num arquivo de implementacao: a 1a linha
        // junta `module X` e `open Y`, o compilador le o resto como corpo do modulo
        // e o topo vira expressao — o `let` seguinte fica fora da posicao (medido no
        // L4 de 2026-10-29, step algo-bridge). A dica mostra SO' a forma que compila.
        if (code == "FS0010" && Regex.IsMatch(compilerError, @"'let'|'use'|'open'"))
        {
            var glued = Regex.Match(source,
                @"(?m)^[ \t]*module[ \t]+(?<m>[\w\.]+)[ \t]+(?<rest>(?:open[ \t]+[\w\.]+[ \t]*(?:[;,][ \t]*)?)+)$");
            if (glued.Success)
            {
                var opens = new List<string>();
                foreach (Match o in Regex.Matches(glued.Groups["rest"].Value, @"open[ \t]+(?<ns>[\w\.]+)"))
                {
                    string ns = "open " + o.Groups["ns"].Value;
                    if (!opens.Contains(ns)) opens.Add(ns);
                }
                return "CERTO: a diretiva do modulo fica sozinha na primeira linha e cada `open` na propria linha:\n"
                       + "module " + glued.Groups["m"].Value + "\n" + string.Join("\n", opens);
            }
        }

        // FS3118/FS0588/FS0528: a funcao foi declarada com `=` e o corpo nao termina
        // numa expressao (let interno engole o resultado, ou o arquivo acaba antes).
        // A forma certa e' um corpo com as linhas alinhadas sob o `=` e a ultima
        // expressao devolvendo o valor (medido no isolado de 2026-10-29).
        if (code is "FS3118" or "FS0588" or "FS0528")
            return "CERTO: o corpo da funcao tem as linhas alinhadas sob o `=` e a ULTIMA expressao devolve o valor:\n"
                   + "let total xs =\n"
                   + "    let dobro = List.map (fun x -> x * 2) xs\n"
                   + "    List.sum dobro";

        // FS0603 (`[|` incompativel): a comprehension de array envolve TUDO entre
        // `[|` e `|]`, com `->` no elemento — a barra nao separa elemento de gerador.
        if (code == "FS0603")
            return "CERTO: a comprehension de array fica inteira entre `[|` e `|]`:\n"
                   + "[| for i in 1 .. 3 -> i * i |]";

        // FS0597: o argumento que e' chamada/pipe precisa de parenteses; sem eles o
        // parser le a expressao como mais um argumento (medido no isolado de
        // 2026-10-29: o modelo repetiu a MESMA linha em 3 builds seguidos).
        if (code == "FS0597")
            return "CERTO: argumento com chamada, pipe ou composicao vai entre parenteses:\n"
                   + "printfn \"%s\" (String.concat \",\" movs)";

        // FS0058 (offside): `let` na propria linha do `else` deixa o corpo fora da
        // coluna permitida. Forma estrutural: auxiliar no nivel do modulo, recebendo
        // por parametro o que ele usa de fora; no ramo, so' a chamada.
        if (code == "FS0058")
        {
            var nested = Regex.Match(source, @"(?m)^[ \t]*else[ \t]+let[ \t]+\w+.*$");
            if (nested.Success)
                return $"ERRADO: {nested.Value.Trim()}\n" +
                       "CERTO: declare o auxiliar no nivel do modulo, ANTES da funcao que o chama, " +
                       "recebendo como parametro os valores que hoje vem de fora (ex.: `let rec check n i = ...`); " +
                       "no ramo, so' a chamada com esses valores";
        }

        // FS0039 com auto-referencia: funcao recursiva sem `rec`. O compilador nomeia
        // o valor que falta; se ele e' um `let` com parametro do proprio arquivo, o que
        // falta e' o `rec` (medido no isolado de 2026-10-29: `let factorial n = ...`).
        if (code == "FS0039")
        {
            var miss = Regex.Match(compilerError, @"constructor '([^']+)' is not defined");
            // `mod` e' OCaml/Pascal: em F# o resto e' `%` (medido no isolado de
            // 2026-10-29, depois de o modelo acertar estrutura e recursao).
            if (miss.Success && miss.Groups[1].Value == "mod")
            {
                var use = Regex.Match(source, @"(?<a>[\w\)\]]+)\s+mod\s+(?<b>[\w\(]+)");
                if (use.Success)
                    return $"ERRADO: {use.Value}\n" +
                           $"CERTO: {use.Groups["a"].Value} % {use.Groups["b"].Value}   (F# nao tem esse operador: o resto e' `%`, so' em inteiros)";
            }
            if (miss.Success)
            {
                string name = miss.Groups[1].Value;
                bool fn = Regex.IsMatch(source, @"^\s*let\s+" + Regex.Escape(name) + @"\s+\w", RegexOptions.Multiline);
                bool hasRec = Regex.IsMatch(source, @"^\s*let\s+rec\s+" + Regex.Escape(name) + @"\b", RegexOptions.Multiline);
                if (fn && !hasRec)
                    return $"ERRADO: let {name} ...\nCERTO: let rec {name} ...   (a auto-chamada exige `rec`)";
            }
            return "";
        }

        // FS0037 nome definido duas vezes: o segundo `let` sai (o arquivo tem sobra).
        if (code == "FS0037")
        {
            var dup = Regex.Match(compilerError, @"Duplicate definition of [^']*'([^']+)'");
            if (dup.Success)
                return $"CERTO: defina '{dup.Groups[1].Value}' UMA vez: apague a definicao repetida (o arquivo ficou com duas)";
        }

        // FS0001 no range: limite float num range int. Converte-se o LIMITE, nao o range.
        if (code == "FS0001"
            && Regex.IsMatch(source, @"\[\s*-?\d+\s*\.\.\s*[^\]\r\n]*(Math\.Sqrt|float\s)"))
            return "CERTO: `[2 .. int (Math.Sqrt(float n))]`   (os dois limites do range sao do MESMO tipo; converta o limite que sai float)";

        // FS0001 `List.*` recebendo `[| ... |]`: o compilador diz lista esperada e
        // array recebido. A dica nomeia a lista (`[...]`) e o `Array.*` correto.
        if (code == "FS0001"
            && compilerError.Contains("list", StringComparison.OrdinalIgnoreCase)
            && compilerError.Contains("array", StringComparison.OrdinalIgnoreCase))
            return "CERTO: `[ a; b ]` (lista) com `List.sum/List.map/List.length`; " +
                   "`[| a; b |]` e' ARRAY: use `Array.sum/Array.map/Array.length`";

        // So' os codigos que este mapa cobre: erro alheio nao recebe conserto de
        // comprehension (evita "corrigir" a linha errada do fonte).
        if (code is not ("FS0003" or "FS0010" or "FS0001" or "FS0020" or "FS0751" or "FS0058"))
            return "";

        // Comprehension de lista: `|` de outra linguagem, `do` sem `yield` e
        // `do`+`->` juntos — as tres formas medidas no L3 de 2026-10-29.
        foreach (Match m in Regex.Matches(source, ComprehensionRx))
        {
            string fix = FixComprehension(m.Groups["body"].Value);
            if (fix.Length > 0)
                return $"ERRADO: {m.Value}\nCERTO: {fix}";
        }

        // `step N` fora de comprehension (ex.: sobra num `[for ...]` ja' com `->`):
        // `step` nao existe em F#, mas o passo e' real — vai no MEIO do range. Vem
        // DEPOIS da comprehension para nao perder a conversao `[| |]`/`|` no caminho.
        if (code is "FS0003" or "FS0010" or "FS0001" or "FS0020" or "FS0751")
        {
            var stepOcc = Regex.Match(source, @"\S+\s*\.\.\s*[^\[\]\r\n]+?\s+step\s+\d+");
            if (stepOcc.Success)
            {
                string fixedRange = StepToRange(stepOcc.Value);
                if (fixedRange != stepOcc.Value)
                    return $"ERRADO: {stepOcc.Value}\nCERTO: {fixedRange}";
            }
        }
        return "";
    }

    /// <summary>Codigo do PRIMEIRO erro do compilador (<c>FS0010</c>...) ou "" se nao houver.</summary>
    /// <param name="compilerError">Saida do build (pode trazer varios erros).</param>
    /// <returns>Ex.: <c>FS0003</c>.</returns>
    static string FirstCode(string compilerError)
    {
        var m = Regex.Match(compilerError, @"error (FS\d{4})");
        return m.Success ? m.Groups[1].Value : "";
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
        // `[| ... |]` e' ARRAY: as barras de borda sao DELIMITADOR, nao operador. O
        // modelo mistura as duas formas (`[| expr | for i in ... step 2 |]`); normalizar
        // a borda antes de ler o corpo evita ensinar `... step 2 | -> | expr` — literal
        // invalido copiado no isolado de 2026-10-29 (DRIVER-FIXES #20).
        string bodyTrim = body.Trim();
        if (bodyTrim.StartsWith("|", StringComparison.Ordinal)) bodyTrim = bodyTrim[1..].Trim();
        if (bodyTrim.EndsWith("|", StringComparison.Ordinal)) bodyTrim = bodyTrim[..^1].Trim();
        // Guarda 1: `yield` em qualquer ponto ja' declara o elemento — forma valida,
        // nao se toca (senao `[for i in r do if c then yield x]` viraria arrow+yield).
        if (Regex.IsMatch(bodyTrim, @"\byield\b")) return "";
        // Guarda 2: so' o que e' conhecidamente suspeito — `|` de outra linguagem ou
        // `do` dentro de lista. Forma apenas com `->` e' F# valido: nao se toca.
        if (!bodyTrim.Contains('|') && !Regex.IsMatch(bodyTrim, @"\sdo\s")) return "";

        // (1) `[expr | for v in range]` — comprehension de Python/Haskell/JS.
        var pipe = Regex.Match(bodyTrim, @"^(?<expr>.+?)\s*\|\s*for\s+(?<v>\w+)\s+in\s+(?<range>.+)$");
        if (pipe.Success)
        {
            string expr = pipe.Groups["expr"].Value.Trim().TrimEnd('|').Trim();
            string pv = pipe.Groups["v"].Value;
            var (range, cond) = SplitDo(pipe.Groups["range"].Value.Trim());
            // `step` nao existe em F#: sem forma conhecida, silencio (nunca ensina `step`).
            if (Regex.IsMatch(range, @"\bstep\b")) return "";
            return cond is null ? $"[for {pv} in {range} -> {expr}]" : Filtered(pv, range, cond, expr);
        }

        // (2) `[for v in range do <tail>]` — `do` dentro de LISTA.
        var doForm = Regex.Match(body, @"^for\s+(?<v>\w+)\s+in\s+(?<range>.+?)\s+do\s+(?<tail>.+)$");
        if (!doForm.Success) return "";
        string v = doForm.Groups["v"].Value;
        string rng = StepToRange(doForm.Groups["range"].Value.Trim());
        string tail = doForm.Groups["tail"].Value.Trim().TrimEnd('|').Trim();
        if (Regex.IsMatch(rng, @"\bstep\b")) return "";   // `step` nao existe em F#
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

    /// <summary>Condicao do comprehension vira <c>do if ... then yield</c> (forma valida).</summary>
    /// <param name="v">Variavel do `for`.</param>
    /// <param name="range">Range original.</param>
    /// <param name="cond">Condicao (com ou sem `if`).</param>
    /// <param name="expr">Expressao do elemento.</param>
    /// <returns>Comprehension valida.</returns>
    static string Filtered(string v, string range, string cond, string expr)
    {
        string c = cond.StartsWith("if ", StringComparison.Ordinal) ? cond[3..].Trim() : cond;
        // Residuo do proprio indice (`... do i`): nao e' teste booleano nem filtro,
        // entao sai. Qualquer outra condicao (ex.: `isAdult x`) e' FILTRO legitimo e
        // vira `do if ... then yield` — nunca descartar condicao em silencio.
        if (Regex.IsMatch(c, @"^\w+$")) return $"[for {v} in {range} -> {expr}]";
        // `=%` e' typo conhecido do modelo (`i =% 2`): normaliza para o resto real.
        c = Regex.Replace(c, @"(\w+)\s*=%\s*2\b", "$1 % 2 <> 0");
        return $"[for {v} in {range} do if {c} then yield {expr}]";
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
        // Bordas de array/comprehension (`|`, `]`) nao fazem parte do range.
        string r = range.Trim().TrimEnd('|', ']').Trim();
        var s = Regex.Match(r, @"^(?<a>\S+)\s*\.\.\s*(?<b>.+?)\s+step\s+(?<st>\d+)$");
        return s.Success ? $"{s.Groups["a"].Value} .. {s.Groups["st"].Value} .. {s.Groups["b"].Value}" : r;
    }
}
