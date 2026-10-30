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
    /// <param name="repeatCount">Quantas vezes ESTA MESMA dica ja' foi entregue antes
    /// (0 na primeira). A partir da 3a (repeatCount &gt;= 2) o Core acrescenta a
    /// instrucao de reescrever o arquivo inteiro — dica identica repetida nao ensina
    /// (medido em 2026-10-29: 17 reparos iguais em 30 turnos).</param>
    /// <returns>"ERRADO: ...\nCERTO: ..." ou string vazia.</returns>
    public static string Suggest(string source, string compilerError, int repeatCount = 0)
    {
        string hint = SuggestForCode(source, compilerError);
        if (hint.Length == 0) return hint;
        hint = WithErrorLine(hint, compilerError);
        if (repeatCount < 2) return hint;
        return hint + "\n\nA MESMA dica ja' veio " + (repeatCount + 1)
             + " vezes: pare de editar a mesma linha e reescreva o arquivo inteiro com a tool `write`, aplicando a forma acima.";
    }

    /// <summary>
    /// Acrescenta a LINHA que o compilador aponta. Medido na 17a amostra do isolado de
    /// 2026-10-29: o erro estava na linha 34 (`[]&lt;string list&gt;`) e o modelo editou
    /// a linha 48 seis vezes — a dica ensinava a forma, mas nao dizia ONDE. Aponta o
    /// numero, nunca o literal errado (o modelo copia o literal que a dica mostra).
    /// </summary>
    /// <param name="hint">Dica ja' montada.</param>
    /// <param name="compilerError">Saida do build com <c>arquivo(linha,coluna)</c>.</param>
    /// <returns>Dica com o ponteiro de linha (ou a propria dica quando nao ha linha).</returns>
    static string WithErrorLine(string hint, string compilerError)
    {
        var where = Regex.Match(compilerError, @"\((\d+),(\d+)\)");
        if (!where.Success) return hint;
        string line = where.Groups[1].Value;
        if (Regex.IsMatch(hint, @"linha " + line + @"\b")) return hint;
        return hint + "\n\nO erro esta na linha " + line
             + " do arquivo: edite ESSA linha (o compilador aponta o lugar exato).";
    }

    static string SuggestForCode(string source, string compilerError)
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

        // FS3156/FS0010 com tipo generico `<...>`: C#-ismo de tipo (17a amostra do
        // isolado de 2026-10-29: `let mutable sequencia = []<string list>` -> FS3156
        // "Unexpected token '>'" na coluna do `>`; o modelo editou OUTRA linha 6 vezes
        // e o erro ficou no lugar). Em F# o tipo vem depois de `:` e e' pos-fixado.
        // Gate pela LINHA do erro (2026-10-30, kmeans: `list<Point>` legitimo no fonte
        // fazia esta dica responder a um `|]` inesperado e o modelo reescrevia o arquivo
        // inteiro com a mesma falha). Sem posicao no erro, a dica segue valendo.
        if ((code == "FS3156" || code == "FS0010")
            && (Regex.IsMatch(source, @"\[\s*\]\s*<") || Regex.IsMatch(source, @":\s*[A-Za-z_][\w.]*\s*<")))
        {
            string errLine = SourceLineAt(source, compilerError);
            if (errLine.Length == 0 || errLine.Contains('<'))
                return "CERTO: F# nao usa `<...>`; a anotacao de tipo vem DEPOIS de `:` e o tipo e' pos-fixado:\n"
                     + "let vazia: string list = []\n"
                     + "let tabela: int array = [| 0; 0 |]\n"
                     + "let rec passos n = if n = 0 then [] else passos (n - 1) @ [ n ]";
        }

        // FS0010 `|]` inesperado pedindo `}`: elemento de array de REGISTRO sem o
        // `}` de fechamento (medido no isolado de 2026-10-30 na missao K-Means F#,
        // que saiu do suite depois — a regra fica: e' sintaxe F#, nao algoritmo:
        // `[| {X=1.0; Y=1.0}; ... {X=9.0; Y=8.0 |]` — 10 turnos, o modelo reescrevia
        // o arquivo inteiro e repetia a falta). O `|]` fecha o ARRAY, depois dos `}`.
        if (code == "FS0010"
            && compilerError.Contains("Unexpected symbol '|]'", StringComparison.Ordinal)
            && compilerError.Contains("'}'", StringComparison.Ordinal))
            return "CERTO: cada registro do array fecha com `}` ANTES do `|]` (o `|]` fecha o ARRAY):\n"
                 + "let points = [| { X = 1.0; Y = 1.0 }; { X = 1.0; Y = 2.0 } |]";

        // FS3373 interpolacao com aspas dentro da expressao: F# recusa aspas dentro
        // do `{...}` quando o literal externo tambem e' string (17a amostra do isolado
        // de 2026-10-29: `printfn $"hanoi_seq={String.concat "," seq}"`). O CERTO
        // separa a expressao num `let` e imprime com formato posicional.
        if (code == "FS3373")
            return "CERTO: monte a expressao num `let` e imprima com formato posicional:\n"
                 + "let rotulo = String.concat \",\" movs\n"
                 + "printfn \"hanoi_seq=%s\" rotulo";

        // FS0101 `^` como POTENCIA: `^` e' concatenacao (obsoleta) e o compilador so'
        // diz "deprecated ... Use the '+' operator instead". Medido no isolado de
        // 2026-10-30 (missao K-Means F#: `(fst p - fst c) ^ 2` em 8 turnos, ZERO
        // dica, porque FS0101 nao tinha mapa). Potencia e' `**` com os dois lados
        // float; inteiro usa `pown`. So' responde com o `^` MEDIDO na linha do erro
        // — outro construto obsoleto nao recebe esta dica.
        if (code == "FS0101" && Regex.IsMatch(SourceLineAt(source, compilerError), @"\^\s*\d"))
            return "CERTO: potencia se escreve `**` e os DOIS lados sao float (o `^` e' concatenacao de texto, obsoleta):\n"
                 + "let distancia (a: int) (b: float) = (float a - b) ** 2.0\n"
                 + "let cubo = pown 2 3   // inteiro: pown base expoente";

        // FS0010 em `seq { ... }` com operador: a comprehension de sequencia exige
        // `yield` (mesma amostra: `sequencia <- seq {+ source, dest}`). Acumular
        // movimento e' LISTA, sem `mutable`/`ref`.
        if (code == "FS0010" && Regex.IsMatch(source, @"seq\s*\{[^}]*[+*]"))
            return "CERTO: `seq { ... }` so' devolve item com `yield`; para acumular use LISTA e `@`:\n"
                 + "let rec passos n = if n = 0 then [] else passos (n - 1) @ [ n ]";

        // FS0010 barra invertida: lambda em F# e' `fun`, nao `\` (9a amostra do
        // isolado de 2026-10-29: `List.fold (\count p1 -> ...) 0 primes`, 0 reparo).
        if (code == "FS0010" && compilerError.Contains("Unexpected character '\\'", StringComparison.Ordinal))
            return "CERTO: lambda em F# se escreve com `fun`:\n"
                 + "List.fold (fun count p1 -> if List.contains (p1 + 2) primes then count + 1 else count) 0 primes";

        // FS0010 "Unexpected type application in pattern": `ref` nao e' tipo prefixo
        // (medido no isolado de 2026-10-29: `(moves: ref seq<string>)` e 26 turnos
        // sem dica). Em F# o tipo e' pos-fixado e o valor se escreve com `:=`.
        if (code == "FS0010" && compilerError.Contains("Unexpected type application", StringComparison.Ordinal))
            return "CERTO: `ref` e' tipo POS-fixado e o valor se escreve com `:=` (le' com `!`):\n"
                 + "let hanoi (moves: string list ref) =\n"
                 + "    moves := !moves @ [ sprintf \"%d->%d\" 1 2 ]";

        // FS0010 "Unexpected symbol ';' in pattern": C#-ismo na lista de parametros
        // (medido na 16a amostra do isolado de 2026-10-29: `(n: int; source: string;
        // ...)` repetido em 4 edits seguidos, ZERO reparo). Em F# o `;` so' separa
        // itens de lista/array/registro; parametros sao curried (espaco) ou tupla (virgula).
        if (code == "FS0010" && compilerError.Contains("Unexpected symbol ';' in pattern", StringComparison.Ordinal))
            return "CERTO: na lista de parametros nao vai `;` — separe por ESPACO (curried) ou por VIRGULA dentro de UM parenteses (tupla):\n"
                 + "let rec hanoi (n: int) (source: int) (destination: int) (auxiliary: int) =\n"
                 + "    if n = 0 then ...   // chamada: hanoi (n - 1) source auxiliary destination\n"
                 + "o `;` so' separa itens de lista, array e registro: `[ 1; 2 ]`, `[| 1; 2 |]`";

        // FS0010 "Unexpected symbol ':' in binding": nome de campo DENTRO da anotacao
        // de retorno (`: (count: int * string list)`). F# nao tem elemento nomeado em
        // anotacao de tipo; medido na 10a amostra do isolado de 2026-10-29
        // (Program.fs(28,91), 5 builds seguidos sem dica). O CERTO e' so' o tipo.
        if (code == "FS0010" && compilerError.Contains("Unexpected symbol ':' in binding", StringComparison.Ordinal))
            return "CERTO: a anotacao de retorno leva SO' o tipo, sem `nome:` dentro dos parenteses:\n"
                 + "let rec hanoi (n: int) (moves: string list) : int * string list =\n"
                 + "    if n = 0 then (0, moves) else hanoi (n - 1) moves";

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

        // FS0010 `->` dentro de lista literal: o `->` so' existe em comprehension.
        // Medido no isolado de 2026-10-29: o modelo escreveu `[s->d]` querendo a
        // tupla (o exemplo de comprehension da tabela induziu o operador). A dica
        // separa as duas formas, cada uma compilavel.
        if (code == "FS0010" && compilerError.Contains("Unexpected symbol '->'", StringComparison.Ordinal)
            && !Regex.IsMatch(source, @"\[[^\]]*\bfor\b"))
            return "CERTO: a lista de elementos leva `;` entre eles e a tupla entre parenteses:\n"
                 + "[ (1, 2); (1, 3) ]\n"
                 + "o `->` so' existe dentro de comprehension: `[ for i in 1 .. 3 -> i * i ]`";

        // FS0752: `expr.[idx]` sobre valor de tipo indeterminado (medido na 15a amostra
        // de 2026-10-29: 7 ocorrencias e ZERO dicas — o modelo indexava o parametro da
        // funcao recursiva sem anotacao). A anotacao do parametro/binding resolve.
        if (code == "FS0752")
            return "CERTO: o valor indexado precisa de tipo CONHECIDO — anote o parametro (ou o binding) antes do `.[i]`:\n"
                 + "let marca (xs: bool array) i = xs.[i]\n"
                 + "let zeros: int array = Array.create 10 0";

        // FS3118/FS0588/FS0528 com `else let` na MESMA linha: o `let` que segue o
        // `else` abre um bloco e precisa da linha propria, com o corpo indentado
        // (medido na 10a amostra do isolado de 2026-10-29: `else let limit = ...`
        // -> FS0588 "The block following this 'let' is unfinished").
        if ((code is "FS3118" or "FS0588" or "FS0528")
            && Regex.IsMatch(source, @"(?m)^[ \t]*else[ \t]+let\b"))
            return "CERTO: o `else` fica sozinho na linha e o `let` do ramo abre um bloco indentado:\n"
                 + "let isPrime n =\n"
                 + "    if n <= 1 then false\n"
                 + "    else\n"
                 + "        let limit = int (Math.Floor(float n / 2.0))\n"
                 + "        List.forall (fun d -> n % d <> 0) [2 .. limit]";

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

        // FS0072: o tipo do parametro nao pode ser inferido (medido no isolado de
        // 2026-10-29, com o programa ja' calculando e a saida so' esperando isso).
        // A forma que compila e' a anotacao no lambda.
        if (code == "FS0072")
            return "CERTO: quando o tipo do parametro nao sai sozinho, anote no lambda:\n"
                 + "List.map (fun (a: int, b: int) -> sprintf \"%d->%d\" a b) movs";

        // FS0193: `@`/`List.append` usados como valor no fim de um pipe ficam sem o
        // segundo operando (medido no isolado de 2026-10-29: o modelo encadeou
        // `|> List.append` tres vezes e repetiu a linha).
        if (code == "FS0193")
            return "CERTO: `@` e `List.append` exigem os DOIS operandos:\n"
                 + "let tudo = a @ b\n"
                 + "let tudo2 = List.append a b\n"
                 + "let tudo3 = x |> List.append ys";

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

                // Valor usado ANTES da definicao no nivel do modulo (medido na 12a
                // amostra de 2026-10-29: `dest_aux` na linha 11 e `let dest_aux = 3` na
                // linha 19). F# le de cima para baixo: mover a definicao para cima do
                // primeiro uso ou receber o valor por parametro.
                var def = Regex.Match(source, @"(?m)^[ \t]*let[ \t]+(?:rec[ \t]+)?" + Regex.Escape(name) + @"\b");
                if (def.Success)
                {
                    int defLine = source.Take(def.Index).Count(ch => ch == '\n') + 1;
                    string[] lines = source.Replace("\r\n", "\n").Split('\n');
                    var useRx = new Regex(@"\b" + Regex.Escape(name) + @"\b");
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i + 1 == defLine) continue;
                        if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                        if (!useRx.IsMatch(lines[i])) continue;
                        if (i + 1 < defLine)
                            return $"ERRADO: '{name}' e' usado na linha {i + 1} e declarado na linha {defLine}\n"
                                 + "CERTO: no modulo a declaracao vem ANTES do primeiro uso (o F# le de cima para baixo) — "
                                 + "mova o `let` para cima ou receba o valor por parametro:\n"
                                 + "let auxiliar = 3\n\n"
                                 + "let rec soma n =\n"
                                 + "    if n = 0 then auxiliar else n + soma (n - 1)";
                        break;
                    }
                }
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

        // FS0001 "ramos do if": os dois ramos devolvem o MESMO tipo, e o conserto
        // depende do tipo que o compilador NOMEIA na mensagem. A dica de array respondia
        // a um ramo `string` e o modelo repetia a linha (14a amostra de 2026-10-29:
        // `subSeq @ "," + nextMove` misturou lista com texto).
        if (code == "FS0001"
            && compilerError.Contains("branches of an 'if' expression", StringComparison.Ordinal))
        {
            // O tipo que decide e' o do PRIMEIRO ramo (o que o compilador nomeia em
            // `which here is '...'`); o segundo pode ser de outro tipo e nao serve de
            // criterio (medido: array x list caia na dica de lista).
            var first = Regex.Match(compilerError, @"which here is '(?<t>.*?)'\.(?:\s|$)");
            string branchType = first.Success ? first.Groups["t"].Value : "";
            if (branchType.Contains("string", StringComparison.Ordinal)
                || (branchType.Length == 0 && compilerError.Contains("'string'", StringComparison.Ordinal)))
                return "CERTO: os dois ramos do `if` devolvem o MESMO tipo, e cada tipo tem o seu jeito de juntar — "
                     + "TEXTO com `+`, LISTA com `@`, ARRAY com `Array.append` (nao misture lista com texto):\n"
                     + "let texto = a + \",\" + b\n"
                     + "let lista = xs @ [ y ]\n"
                     + "let arr = Array.append a b";
            if (branchType.Contains("list", StringComparison.Ordinal))
                return "CERTO: os dois ramos do `if` devolvem o MESMO tipo — para juntar LISTA use `@`:\n"
                     + "if cond then [ (a, b) ] else xs @ [ (a, b) ]";
            return "CERTO: os dois ramos do `if` devolvem o MESMO tipo — para juntar ARRAY use `Array.append`:\n"
                 + "if cond then [| (a, b) |] else Array.append antes depois";
        }

        // FS0001 lista x texto: `@` concatena LISTA e `+` concatena TEXTO — misturar os
        // dois e' o mesmo defeito visto por outro angulo (14a amostra de 2026-10-29).
        if (code == "FS0001"
            && compilerError.Contains("''a list'", StringComparison.Ordinal)
            && compilerError.Contains("'string'", StringComparison.Ordinal))
            return "CERTO: `@` junta LISTA e `+` junta TEXTO — nao misture os dois:\n"
                 + "let texto = a + \",\" + b\n"
                 + "let lista = xs @ [ y ]";

        // FS0001 `List.*` recebendo `[| ... |]`: o compilador diz lista esperada e
        // array recebido. A dica nomeia a lista (`[...]`) e o `Array.*` correto.
        if (code == "FS0001"
            && compilerError.Contains("list", StringComparison.OrdinalIgnoreCase)
            && compilerError.Contains("array", StringComparison.OrdinalIgnoreCase))
            return "CERTO: `[ a; b ]` (lista) com `List.sum/List.map/List.length`; " +
                   "`[| a; b |]` e' ARRAY: use `Array.sum/Array.map/Array.length`";

        // FS0001 no range: limite float num range int. Converte-se o LIMITE, nao o
        // range. A mensagem precisa citar os dois tipos: sem essa evidencia o codigo
        // FS0001 e' outro caso (em 2026-10-29 a dica de range respondeu a um erro de
        // ramos de `if` e o modelo repetiu a linha 4x).
        if (code == "FS0001"
            && compilerError.Contains("float", StringComparison.Ordinal)
            && compilerError.Contains("int", StringComparison.Ordinal)
            && Regex.IsMatch(source, @"\[\s*-?\d+\s*\.\.\s*[^\]\r\n]*(Math\.Sqrt|float\s)"))
            return "CERTO: `[2 .. int (Math.Sqrt(float n))]`   (os dois limites do range sao do MESMO tipo; converta o limite que sai float)";

        // FS0001 ORDEM de argumento de `List.contains`: a assinatura e'
        // `'T -> 'T list -> bool`, entao `List.contains lista valor` faz o compilador
        // pedir `'T list list`. Medido no isolado de 2026-10-30 (E-mission-algo-fs-btree:
        // `List.contains node.Keys key`, 8 turnos sem dica e o fonte nunca corrigido).
        // Gate ESTREITO: o tipo esperado precisa terminar em `list list` E a linha do
        // erro precisa chamar `List.contains` — sem os dois nao ha medida.
        if (code == "FS0001"
            && Regex.IsMatch(compilerError, @"expected to have type\s+'[^']*\blist\s+list'\s")
            && SourceLineAt(source, compilerError).Contains("List.contains", StringComparison.Ordinal))
            return "CERTO: `List.contains` recebe o VALOR e depois a LISTA (`List.contains valor lista`) — a lista e' o ULTIMO argumento:\n"
                 + "let existe = List.contains key node.Keys";

        // FS0043 `==` em F#: F# usa `=` para igualdade (um sinal). `==` nao existe
        // em F# (C#-ismo). Medido em 2026-10-30 (E-mission-algo-fs-btree, linha 65
        // `if value == node.Value then`). Dica estreita: so' quando a mensagem cita
        // o operador '=='.
        if (code == "FS0043"
            && compilerError.Contains("operator '=='", StringComparison.Ordinal))
            return "CERTO: em F# igualdade e' um sinal so' - `=` (o operador duplo e' de outra linguagem):\\n"
                 + "if value = node.Value then true else ...";

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
        // FS0003 "This value is not a function and cannot be applied": a chamada passa
        // mais argumentos do que a declaracao do PROPRIO arquivo aceita (medido na 11a
        // amostra de 2026-10-29: `let rec hanoi (n: int) : int * string list` chamado
        // como `hanoi n 1 3 2`). Vem DEPOIS do `step` para nao roubar o caso do range e
        // so' responde quando a divergencia e' MEDIDA na fonte — sem medida, silencio.
        if (code == "FS0003"
            && compilerError.Contains("not a function and cannot be applied", StringComparison.Ordinal))
        {
            var arity = FindArityMismatch(source, compilerError);
            if (arity is not null)
                return $"ERRADO: '{arity.Value.Name}' foi declarada com {arity.Value.Declared} parametro(s) "
                     + $"e a chamada da linha {arity.Value.Line} passa {arity.Value.Passed}\n"
                     + "CERTO: a declaracao e a chamada usam a MESMA quantidade de parametros — "
                     + "se a chamada passa 3 valores, a funcao declara 3:\n"
                     + "let rec soma (a: int) (b: int) (c: int) : int =\n"
                     + "    if c = 0 then a + b else soma a b (c - 1)\n"
                     + "let r = soma 1 2 3";
        }
        return "";
    }

    /// <summary>
    /// Numero da linha que o compilador aponta (<c>arquivo(linha,coluna)</c>) ou 0
    /// quando a saida nao traz posicao.
    /// </summary>
    /// <param name="compilerError">Saida do build.</param>
    static int SourceLineNumber(string compilerError)
    {
        var where = Regex.Match(compilerError, @"\((\d+),(\d+)\)");
        return where.Success && int.TryParse(where.Groups[1].Value, out int lineNo) ? lineNo : 0;
    }

    /// <summary>
    /// Texto da linha que o compilador aponta (vazio quando nao ha linha no erro).
    /// Fonte unica da leitura: as regras que dependem da LINHA medem por aqui.
    /// </summary>
    /// <param name="source">Fonte F# atual.</param>
    /// <param name="compilerError">Saida do build (traz <c>arquivo(linha,coluna)</c>).</param>
    static string SourceLineAt(string source, string compilerError)
    {
        int lineNo = SourceLineNumber(compilerError);
        if (lineNo < 1) return "";
        var lines = source.Replace("\r\n", "\n").Split('\n');
        return lineNo <= lines.Length ? lines[lineNo - 1] : "";
    }

    /// <summary>
    /// Divergencia de aridade entre a declaracao (no proprio fonte) e a chamada da
    /// linha do erro. Devolve <c>null</c> quando nao consegue MEDIR — nunca chuta.
    /// </summary>
    /// <param name="source">Fonte F# atual.</param>
    /// <param name="compilerError">Saida do build (traz <c>arquivo(linha,coluna)</c>).</param>
    static (string Name, int Declared, int Passed, int Line)? FindArityMismatch(string source, string compilerError)
    {
        int lineNo = SourceLineNumber(compilerError);
        string line = SourceLineAt(source, compilerError);
        if (lineNo < 1 || line.Length == 0) return null;
        foreach (Match cand in Regex.Matches(line, @"[A-Za-z_]\w*"))
        {
            string name = cand.Value;
            var decl = Regex.Match(source,
                @"(?m)^[ \t]*let[ \t]+(?:rec[ \t]+)?" + Regex.Escape(name) + @"\b(?<params>[^\r\n=]*)");
            if (!decl.Success) continue;
            int declared = CountParams(decl.Groups["params"].Value);
            if (declared == 0) continue;                    // valor, nao funcao
            int passed = CountArgs(line, cand.Index + name.Length);
            if (passed > declared) return (name, declared, passed, lineNo);
        }
        return null;
    }

    /// <summary>
    /// Parametros declarados: um por grupo entre parenteses ou por nome solto antes de
    /// <c>:</c> (anotacao de retorno) ou do fim do trecho.
    /// </summary>
    static int CountParams(string text)
    {
        int count = 0, depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '(') { if (depth == 0) count++; depth++; continue; }
            if (ch == ')') { if (depth > 0) depth--; continue; }
            if (depth > 0) continue;
            if (ch == ':') break;                            // `: tipo` e' o retorno
            if (ch == '<') { while (i + 1 < text.Length && text[i + 1] != '>') i++; i++; continue; }
            if (char.IsLetter(ch) || ch == '_')
            {
                count++;
                while (i + 1 < text.Length
                       && (char.IsLetterOrDigit(text[i + 1]) || text[i + 1] == '_' || text[i + 1] == '\'')) i++;
            }
        }
        return count;
    }

    /// <summary>Argumentos da chamada a partir de <c>from</c> (um por atomo, grupo conta 1).</summary>
    static int CountArgs(string line, int from)
    {
        int count = 0, depth = 0;
        for (int i = from; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (ch == '(' || ch == '[' || ch == '{') { if (depth == 0) count++; depth++; continue; }
            if (ch == ')' || ch == ']' || ch == '}') { if (depth == 0) break; depth--; continue; }
            if (depth > 0) continue;
            if (ch == '|' || ch == ';' || ch == ',' || ch == '=') break;
            if (ch == '"')
            {
                count++;
                for (i++; i < line.Length && line[i] != '"'; i++) if (line[i] == '\\') i++;
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i;
                while (i + 1 < line.Length
                       && (char.IsLetterOrDigit(line[i + 1]) || line[i + 1] == '_' || line[i + 1] == '\'')) i++;
                string word = line.Substring(start, i - start + 1);
                if (word is "then" or "else" or "in" or "let" or "do" or "match" or "with" or "fun" or "if" or "of") break;
                count++;
                continue;
            }
            if (char.IsDigit(ch))
            {
                count++;
                while (i + 1 < line.Length && (char.IsLetterOrDigit(line[i + 1]) || line[i + 1] == '.')) i++;
            }
        }
        return count;
    }

    /// <summary>Codigo do PRIMEIRO erro do compilador (<c>FS0010</c>...) ou "" se nao houver.</summary>
    /// <param name="compilerError">Saida do build (pode trazer varios erros).</param>
    /// <returns>Ex.: <c>FS0003</c>.</returns>
    /// <summary>
    /// Ha diagnostico F# com codigo na saida do build? Vale para `error` e
    /// `warning`: o FS0193 sai como warning e o chamador que so' olhava `error`
    /// deixava de injetar a dica (medido em 2026-10-29 — 4 builds sem reparo).
    /// </summary>
    public static bool HasDiagnostic(string output)
        => !string.IsNullOrEmpty(output)
           && Regex.IsMatch(output, @"(?:error|warning) (FS\d{4})");

    static string FirstCode(string compilerError)
    {
        // Erro primeiro; warning so' quando ele e' o unico diagnostico com codigo
        // (medido em 2026-10-29: FS0193 saiu como warning e o reparo nunca disparava).
        var m = Regex.Match(compilerError, @"error (FS\d{4})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(compilerError, @"warning (FS\d{4})");
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
