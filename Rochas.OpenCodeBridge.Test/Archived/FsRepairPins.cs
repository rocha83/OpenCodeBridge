using System.Threading.Tasks;

namespace Rochas.OpenCodeBridge.Test.Archived;

/// <summary>
/// Pins do reparo de sintaxe F# — ARQUIVADOS em 2026-10-30 junto da trilha F#
/// (ver <c>README.md</c> desta pasta). O código compila de propósito, para
/// preservar a tabela e os enunciados, mas NÃO é registrado no <c>Program.cs</c>:
/// teste arquivado não entra no placar. A trilha ativa é C# (<c>CsSyntaxRepair</c>).
/// </summary>
internal static class FsRepairPins
{
    public static Task FsRepairComprehensionForms()
    {
        static string Fs(string src, string err)
            => global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        const string fs0010Pipe = "error FS0010: Unexpected symbol '|' in expression. Expected ']' or other token.";
        const string fs0010Arrow = "error FS0010: Unexpected symbol '->' in expression.";
        const string fs0001Unit = "error FS0001: The type 'unit' does not support the operator '+'";
        const string fs0039Math = "error FS0039: The namespace 'Math' is not defined.";

        // 1) Caso historico (#13): '|' de outra linguagem, com `do i` sobrando no range.
        string h1 = Fs("let xs = [f (a + float i * h) | for i in 1 .. n - 1 do i]", fs0010Pipe);
        TestContext.Check(h1.Contains("[for i in 1 .. n - 1 -> f (a + float i * h)]"),
            "pipe -> forma com '->' (veio: " + h1.Replace("\n", " | ") + ")");
        TestContext.Check(!h1.Contains("do i ->"), "CERTO nao mistura 'do' com '->'");

        // 2) Caso historico: `step` no range vira passo no MEIO.
        string h2 = Fs("let xs = [f i | for i in 1 .. n-1 step 2]", fs0010Pipe);
        TestContext.Check(h2.Contains("[for i in 1 .. 2 .. n-1 -> f i]"),
            "range com 'step' -> passo no meio (veio: " + h2.Replace("\n", " | ") + ")");

        // 3) Caso historico: comprehension sem filtro, Haskell/Python puro.
        string h3 = Fs("let sq = [ (x - mean) ** 2.0 | for x in data ]", fs0010Pipe);
        TestContext.Check(h3.Contains("[for x in data -> (x - mean) ** 2.0]"), "pipe simples -> '->'");

        // 4) `do` sem yield dentro de lista (FS0001 'unit') -> '->' com if/then/else.
        string n1 = Fs("let s = [for i in 1 .. n-1 do if i % 2 <> 0 then f (a + float i * h) else 0.0]", fs0001Unit);
        TestContext.Check(n1.Contains("[for i in 1 .. n-1 -> if i % 2 <> 0 then f (a + float i * h) else 0.0]"),
            "'do' puro -> '->' preservando o if/then/else");

        // 5) `do` + `->` juntos: a condicao continua sendo FILTRO (`do if ... then
        // yield`). Nao convertemos paridade em passo — foi essa conversao que ensinou
        // `1 .. n-1 .. 2` (ordem trocada, resultado errado) no isolado de 2026-10-29.
        string n2 = Fs("let s = [for i in 1 .. n - 1 do i =% 2 -> 4.0 * f (float i * h)]", fs0010Arrow);
        TestContext.Check(n2.Contains("[for i in 1 .. n - 1 do if i % 2 <> 0 then yield 4.0 * f (float i * h)]"),
            "do+-> com condicao -> 'do if ... then yield' (veio: " + n2.Replace("\n", " | ") + ")");
        string n3 = Fs("let e = [4.0 * f i | for i in 2 .. n - 2 do i % 2 = 0]", fs0010Pipe);
        TestContext.Check(n3.Contains("[for i in 2 .. n - 2 do if i % 2 = 0 then yield 4.0 * f i]"), "condicao vira filtro no yield");

        // 5) array-comprehension do modelo (`[| expr | for i in ... step 2 |]`): o
        // reparo antigo devolvia `[for i in 1 .. n - 1 step 2 | -> | expr]` (lixo com
        // `step` e barras) e o modelo copiava; medido no isolado de 2026-10-29.
        string n7 = Fs("let so = List.sum [| 2.0 * f (a + float i * h) | for i in 1 .. n - 1 step 2 |]",
            "error FS0010: Unexpected symbol '|' in expression. Expected '|]' or other token.");
        TestContext.Check(n7.Contains("[for i in 1 .. 2 .. n - 1 -> 2.0 * f (a + float i * h)]"),
            "array-comprehension -> lista com passo no meio (veio: " + n7.Replace("\n", " | ") + ")");
        // So' o CERTO e' contrato: o ERRADO ecoa o fonte do modelo (que tem `step`).
        string certo7 = n7[(n7.IndexOf("CERTO: ", StringComparison.Ordinal) + 7)..];
        TestContext.Check(!certo7.Contains("step"), "CERTO nunca ensina `step` (nao existe em F#)");
        TestContext.Check(!certo7.Contains("| ->"), "CERTO nao carrega a barra do array para o arrow");
        string n8 = Fs("let se = List.sum [| 2.0 * f (float i) | for i in 2 .. n - 2 step 2]",
            "error FS0010: Unexpected symbol '|' in expression. Expected '|]' or other token.");
        TestContext.Check(n8.Contains("[for i in 2 .. 2 .. n - 2 -> 2.0 * f (float i)]"),
            "array-comprehension sem barra final -> lista (veio: " + n8.Replace("\n", " | ") + ")");

        // 6) `open System.Math` (FS0039): sem conserto deterministico -> silencio.
        string n4 = Fs("module Program\nopen System.Math\nlet x = Sin 1.0", fs0039Math);
        TestContext.Check(n4 == "", "FS0039 sozinho nao recebe conserto inventado (silencio)");

        // 6a) `step` no range sai como FS0003: normaliza pela FONTE (passo no MEIO).
        string n9 = Fs("let so = [for i in 1 .. n-1 step 2 -> i]",
            "error FS0003: This value is not a function and cannot be applied. It has type 'int', which does not accept arguments.");
        TestContext.Check(n9.Contains("CERTO: 1 .. 2 .. n-1"), "FS0003 com 'step' -> passo no MEIO (veio: " + n9.Replace("\n", " | ") + ")");

        // 6b2) Saida multi-erro: quem decide e' o PRIMEIRO codigo. FS0039 na frente
        // com FS0001 (list/array) depois NAO pode virar dica de array.
        TestContext.Check(Fs("let data = [|2.0|]\nlet d = [for x in data -> x]",
            "error FS0039: The value or constructor 'data' is not defined.\nerror FS0001: This expression was expected to have type ''a list' but here has type ''b array'") == "",
            "primeiro erro FS0039 silencia (nao responde com dica de array)");

        // 6b4) Crase simples em nome/operador (FS3563): `mod` e' operador puro.
        string n11 = Fs("let rec gcd a b = if b = 0 then a else gcd b (a `mod` b)",
            "error FS3563: This is not a valid identifier");
        TestContext.Check(n11.Contains("CERTO: mod") && !n11.Contains("CERTO: `mod`"),
            "FS3563 -> operador sem crase (veio: " + n11.Replace("\n", " | ") + ")");

        // 6b6) `mod` (OCaml/Pascal) em F#: FS0039 nomeia o operador -> resto e' `%`.
        string n13 = Fs("let rec gcd a b = if b = 0 then a else gcd b (a mod b)",
            "error FS0039: The value or constructor 'mod' is not defined.");
        TestContext.Check(n13.Contains("CERTO: a % b"), "FS0039 'mod' -> operador % (veio: " + n13.Replace("\n", " | ") + ")");

        // 6b7) Nome definido duas vezes (FS0037) e range com limite float (FS0001).
        TestContext.Check(Fs("let isPrime n = n > 1\nlet isPrime n = n > 2",
            "error FS0037: Duplicate definition of value 'isPrime'").Contains("UMA vez"),
            "FS0037 -> nome definido uma vez");
        TestContext.Check(Fs("let ps = [1..System.Math.Sqrt(float n)]",
            "error FS0001: This expression was expected to have type 'int' but here has type 'float'").Contains("int (Math.Sqrt(float n))"),
            "FS0001 no range -> converte o limite");

        // 6b8) FS0001 com a MESMA mensagem de outro caso (ramos do `if`): a dica e'
        //      a do `if` — array junta com Array.append — e nao a do range (medido no
        //      isolado de 2026-10-29).
        string ramos = Fs("let rec h n =\n    if n = 1 then [| (1, 3) |]\n    else let a = h (n - 1)\n         [| (1, 2) |] @ a",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is '('a * 'a) array'. This branch returns a value of type ''b list'.");
        TestContext.Check(ramos.Contains("Array.append"),
            "FS0001 ramos do if -> array junta com Array.append (veio: " + ramos.Replace("\n", " | ") + ")");
        TestContext.Check(!ramos.Contains("Math.Sqrt"),
            "FS0001 ramos do if nao recebe a dica de range");
        TestContext.Check(Fs("let ps = [1..System.Math.Sqrt(float n)]",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch").Contains("Array.append"),
            "FS0001 com mensagem de if vence o range mesmo com range no fonte");

        // 6b5) `let` aninhado na linha do `else` (FS0058): o auxiliar vai para o
        // nivel do modulo, recebendo por parametro o que usa de fora.
        string n12 = Fs("let isPrime n =\n    if n < 2 then false\n    else let rec check i = i * i > n\n    in check 5",
            "error FS0058: Unexpected syntax or possible incorrect indentation: this token is offside of context started at position (20:10).");
        TestContext.Check(n12.Contains("ERRADO: else let rec check i") && n12.Contains("nivel do modulo"),
            "FS0058 -> auxiliar no nivel do modulo (veio: " + n12.Replace("\n", " | ") + ")");

        // 6b3) Auto-referencia sem `rec`: o nome que falta e' um `let` com parametro
        // do proprio arquivo -> falta `rec` (nao e' valor nem identificador inventado).
        string n10 = Fs("let factorial n = if n = 0 then 1 else n * factorial (n - 1)\nlet x = factorial 10",
            "error FS0039: The value or constructor 'factorial' is not defined.");
        TestContext.Check(n10.Contains("CERTO: let rec factorial"), "FS0039 de auto-referencia -> `let rec` (veio: " + n10.Replace("\n", " | ") + ")");
        TestContext.Check(Fs("let rec f n = if n = 0 then 1 else n * f (n - 1)",
            "error FS0039: The value or constructor 'f' is not defined.") == "", "ja' com `rec` nao e' tocado");

        // 6b) Filtro legitimo sem operador NAO pode ser descartado (perderia a
        // condicao em silencio: compila e calcula errado).
        string n5 = Fs("let a = [for x in xs do isAdult x -> x]", fs0010Arrow);
        TestContext.Check(n5.Contains("[for x in xs do if isAdult x then yield x]"),
            "condicao sem operador vira 'do if ... then yield' (veio: " + n5.Replace("\n", " | ") + ")");
        // 6c) Erro fora do mapa nao recebe conserto de comprehension (linha errada).
        TestContext.Check(Fs("let a = [x | for x in xs]", "error FS0410: The type 'int' is not compatible") == "",
            "erro alheio nao recebe conserto de comprehension");

        // 6d) `[| ... |]` (array) entregue a `List.sum` (FS0001 list vs array): a dica
        // nomeia a lista e o Array.* correto — 21 turnos perdidos no isolado de
        // 2026-10-29 reescrevendo o mesmo arquivo sem entender o erro.
        string n6 = Fs("let dataList = [|2.0; 4.0|]\nlet sum = List.sum dataList",
            "error FS0001: This expression was expected to have type    ''a list'    but here has type    ''b array'");
        TestContext.Check(n6.Contains("Array.sum"), "FS0001 list/array ensina Array.sum");
        TestContext.Check(n6.Contains("[ a; b ]"), "FS0001 list/array mostra a lista");

        // 7) Negativos: forma JA' valida nao pode ser 'corrigida'.
        TestContext.Check(Fs("let xs = [for i in 1 .. 3 -> float i]", fs0010Arrow) == "",
            "arrow puro (valido) nao e' tocado");
        TestContext.Check(Fs("let xs = [for i in 1 .. n do if i % 2 <> 0 then yield float i]", fs0010Arrow) == "",
            "do + yield (valido) nao e' tocado");
        TestContext.Check(Fs("let xs = [1 .. 2 .. 9]", "error FS0010: x") == "", "lista sem comprehension nao e' tocada");
        return Task.CompletedTask;
    }

    public static Task FsRepairModuleLineAlone()
    {
        const string err = "error FS0010: Unexpected keyword 'let' or 'use' in implementation file";
        string glued = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "module Program open System\n\n// teste\nlet soma a b = a + b\n", err);
        TestContext.Check(glued.Contains("module Program\nopen System"),
            "module + open colados -> uma diretiva por linha (veio: " + glued.Replace("\n", " | ") + ")");
        TestContext.Check(!glued.Contains("module Program open"), "a dica nao repete a forma que nao compila");
        // Dois `open` na mesma linha do modulo: cada um sai na sua linha.
        string dois = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "module Program open System open System.IO\nlet x = 1\n", err);
        TestContext.Check(dois.Contains("module Program\nopen System\nopen System.IO"),
            "dois `open` -> uma linha cada (veio: " + dois.Replace("\n", " | ") + ")");
        // Arquivo ja' com o cabecalho correto: nenhuma dica inventada.
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "module Program\n\nopen System\n\nlet soma a b = a + b\n", err) == "",
            "cabecalho ja' correto nao recebe dica");
        // O MESMO cabecalho colado reaparece como 'open' inesperado quando a linha de
        // baixo tambem e' um open (medido no isolado de 2026-10-29, T2/T3).
        string asOpen = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "module Program open System\nopen System.Linq\n\nlet x = 1\n",
            "error FS0010: Unexpected keyword 'open' in implementation file");
        TestContext.Check(asOpen.Contains("module Program\nopen System"),
            "'open' inesperado no cabecalho colado -> mesma dica (veio: " + asOpen.Replace("\n", " | ") + ")");
        TestContext.Check(!asOpen.Contains("module Program open"), "a dica do 'open' tambem nao repete a forma colada");
        return Task.CompletedTask;
    }

    public static Task FsRepairFunctionBodyShape()
    {
        static string Fs(string src, string err)
            => global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        string corpo = Fs("let primesCount n =\n    let countPrimes limit =\n        limit\n",
            "error FS3118: Incomplete value or function definition.");
        TestContext.Check(corpo.Contains("let total xs =\n    let dobro") && corpo.Contains("List.sum dobro"),
            "FS3118 -> corpo com let local e ultima expressao como valor (veio: " + corpo.Replace("\n", " | ") + ")");
        string inacabado = Fs("let f n =\n    let g = n\n",
            "error FS0588: The block following this 'let' is unfinished.");
        TestContext.Check(inacabado.Contains("List.sum dobro"), "FS0588 -> mesma forma de corpo");
        string eof = Fs("let f n =\n    let g = n\n", "error FS0528: Unexpected end of input");
        TestContext.Check(eof.Contains("List.sum dobro"), "FS0528 -> mesma forma de corpo");
        string arr = Fs("let s = [| if p i then 1 else 0 | for i in 2 .. n |]",
            "error FS0603: ''[|'' incompatible.");
        TestContext.Check(arr.Contains("[| for i in 1 .. 3 -> i * i |]"),
            "FS0603 -> comprehension de array inteira entre [| e |] (veio: " + arr.Replace("\n", " | ") + ")");
        TestContext.Check(!arr.Contains("| for i in 2 .."), "a dica nao repete a barra no meio");
        return Task.CompletedTask;
    }

    public static Task FsRepairCallArgumentParens()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rotulo movs = printfn \"%s\" String.concat \",\" movs\n",
            "error FS0597: This argument expression needs parentheses. Expressions involving function or method calls must be parenthesized when passed as arguments, e.g. 'printfn \"%s\" (arg.Trim())'.");
        TestContext.Check(p.Contains("(String.concat \",\" movs)"),
            "FS0597 -> argumento entre parenteses (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("printfn \"%s\" (") && !p.Contains("\" String.concat"),
            "FS0597 -> a dica mostra so a forma que compila");
        return Task.CompletedTask;
    }

    public static Task FsRepairInferredTypeAnnotation()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "printfn \"%s\" (String.concat \",\" (List.map (fun move -> sprintf \"%d->%d\" move.Item1 move.Item2) movs))\n",
            "error FS0072: The type of this expression cannot be determined here, as no further information is available.");
        TestContext.Check(p.Contains("(a: int, b: int)") && p.Contains("List.map"),
            "FS0072 -> anotacao no lambda (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("sprintf \"%d->%d\" a b"), "FS0072 -> tuple desmontado com a e b");
        return Task.CompletedTask;
    }

    public static Task FsRepairListLiteralArrow()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec solve k =\n    if k = 1 then\n        [s->d]\n    else\n        [s->d]\n",
            "error FS0010: Unexpected symbol '->' in expression. Expected ']' or other token.");
        TestContext.Check(p.Contains("[ (1, 2); (1, 3) ]"),
            "FS0010 '->' em lista -> lista com ; e tupla entre parenteses (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("[ for i in 1 .. 3 -> i * i ]"),
            "a dica separa comprehension (onde o -> existe) da lista literal");
        TestContext.Check(!p.Contains("[s->d]"), "a dica nao ecoa a forma errada");
        // Com comprehension no fonte (`for ... in`) a dica continua sendo a da
        // comprehension: o `->` ali e' legal e o defeito e' outro (`do`, `|`, `step`).
        string comp = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let s = [for i in 1 .. n - 1 do i =% 2 -> 4.0 * f (float i * h)]",
            "error FS0010: Unexpected symbol '->' in expression.");
        TestContext.Check(comp.Contains("do if") || comp.Contains("yield"),
            "FS0010 '->' com comprehension mantem a dica de comprehension (veio: " + comp.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    public static Task FsRepairPartialOperator()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec hanoi n a b c =\n    if n = 1 then [ a, b ]\n    else hanoi (n - 1) a c b |> List.append\n",
            "warning FS0193: This expression is a function value, i.e. is missing arguments. Its type is (^a * ^b) list -> (^a * ^b) list.");
        TestContext.Check(p.Contains("a @ b") && p.Contains("List.append a b"),
            "FS0193 -> operador exige os dois operandos (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("x |> List.append ys"),
            "FS0193 -> no pipe o segundo operando vem explicito");
        TestContext.Check(!p.Contains("|> List.append\n"), "a dica nao ecoa o pipe sem o segundo operando");
        return Task.CompletedTask;
    }

    public static Task FsRepairDiagnosticDetection()
    {
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.HasDiagnostic(
            "ERRO: exit 1: /x/Program.fs(14,9): warning FS0193: This expression is a function value."),
            "warning FSdddd conta como diagnostico (era o caso que ficava sem dica)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.HasDiagnostic(
            "ERRO: exit 1: /x/Program.fs(3,1): error FS0010: Unexpected symbol."),
            "error FSdddd conta como diagnostico");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.HasDiagnostic(
            "ERRO: exit 1: MSBUILD : error MSB1009: Project file does not exist."),
            "erro sem codigo FS nao dispara reparo de sintaxe F#");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.HasDiagnostic(""),
            "saida vazia nao dispara reparo");
        return Task.CompletedTask;
    }

    public static Task FsRepairRefType()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec hanoi (n: int) (moves: ref seq<string>) =\n    if n = 1 then ()\n",
            "error FS0010: Unexpected type application  in pattern. Expected ')' or other token.");
        TestContext.Check(p.Contains("string list ref"),
            "FS0010 ref -> tipo pos-fixado (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains(":=") && p.Contains("!moves"),
            "FS0010 ref -> escrita com `:=` e leitura com `!`");
        TestContext.Check(!p.Contains("ref seq"), "a dica nao ecoa a forma prefixa errada");
        return Task.CompletedTask;
    }

    public static Task FsRepairRepeatEscalation()
    {
        const string err = "error FS0010: Unexpected symbol '->' in expression. Expected ']' or other token.";
        const string src = "let rec solve k =\n    if k = 1 then\n        [s->d]\n    else\n        [s->d]\n";
        string first = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        string third = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err, 2);
        TestContext.Check(first.Length > 0 && !first.Contains("reescreva o arquivo inteiro"),
            "1a dica sem escalada (veio: " + first.Replace("\n", " | ") + ")");
        TestContext.Check(third.StartsWith(first, StringComparison.Ordinal),
            "3a dica mantem a forma certa e acrescenta a escalada");
        TestContext.Check(third.Contains("reescreva o arquivo inteiro") && third.Contains("`write`"),
            "3a dica manda reescrever o arquivo com write");
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, "MSBUILD : error MSB1009", 5) == "",
            "sem dica conhecida nao ha escalada (nao inventa instrucao)");
        return Task.CompletedTask;
    }

    public static Task FsRepairLambdaBackslash()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let twins = List.fold (\\count p1 -> if List.contains (p1 + 2) primes then count + 1 else count) 0 primes\n",
            "error FS0010: Unexpected character '\\' in expression");
        TestContext.Check(p.Contains("fun count p1 ->"),
            "FS0010 barra invertida -> lambda com fun (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("List.fold (fun") && !p.Contains("\\count"),
            "a dica nao ecoa a barra invertida");
        return Task.CompletedTask;
    }

    public static Task FsRepairReturnTupleAnnotation()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec hanoi_calc (n: int) (source: int) : (count: int * string list) =\n    if n = 0 then (0, []) else hanoi_calc (n - 1) source\n",
            "error FS0010: Unexpected symbol ':' in binding. Expected ')' or other token.");
        TestContext.Check(p.Contains(": int * string list ="),
            "FS0010 ':' em binding -> anotacao leva so' o tipo (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let rec hanoi (n: int)"), "a dica mostra a assinatura que compila");
        TestContext.Check(!p.Contains("count:"), "a dica nao ecoa `nome:` dentro da anotacao");
        return Task.CompletedTask;
    }

    public static Task FsRepairElseLetBlock()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let isPrime n =\n    if n <= 1 then false\n    else let limit = int (Math.Floor(float n / 2.0))\n    List.forall (fun d -> n % d <> 0) [2 .. limit]\n",
            "error FS0588: The block following this 'let' is unfinished. Every code block is an expression and must have a result.");
        TestContext.Check(p.Contains("else\n        let limit"),
            "FS0588 `else let` -> `else` sozinho e bloco indentado (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("else let"), "a dica nao ecoa `else let` na mesma linha");
        TestContext.Check(p.Contains("List.forall (fun d -> n % d <> 0)"), "a dica devolve o corpo completo que compila");
        string corpo = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let f n =\n    let g = n\n",
            "error FS0588: The block following this 'let' is unfinished. Every code block is an expression and must have a result.");
        TestContext.Check(corpo.Contains("List.sum dobro"), "FS0588 sem `else let` mantem a dica de corpo (nada de regressao)");
        return Task.CompletedTask;
    }

    public static Task FsRepairArityMismatch()
    {
        string src = string.Join("\n", new[]
        {
            "module Program", "", "open System", "",
            "let rec hanoi (n: int) : int * string list =",
            "    if n = 0 then (0, [])",
            "    else",
            "        let (m1, s1) = hanoi n 1 3 2",
            "        (m1 + 1, s1)",
            "",
            "printfn \"%A\" (hanoi 4)"
        });
        string err = "/tmp/e2e-mission-algo-fs/mfs/Program.fs(8,24): error FS0003: "
                   + "This value is not a function and cannot be applied. It has type '(int * string list)'";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("'hanoi' foi declarada com 1 parametro") && p.Contains("passa 4"),
            "FS0003 aridade -> a dica mede declaracao x chamada (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let rec soma (a: int) (b: int) (c: int)"), "a dica mostra a forma que compila");
        TestContext.Check(!p.Contains("hanoi n 1 3 2"), "a dica nao ecoa a chamada errada");

        string ok = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec soma a b = a + b\nlet r = soma 1 2\n",
            "/tmp/p.fs(2,9): error FS0003: This value is not a function and cannot be applied.");
        TestContext.Check(ok.Length == 0, "aridade coerente nao recebe dica de aridade (veio: " + ok.Replace("\n", " | ") + ")");

        string semPos = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            src, "error FS0003: This value is not a function and cannot be applied. It has type '(int * string list)'");
        TestContext.Check(semPos.Length == 0, "FS0003 sem linha/coluna se cala (nao chuta aridade)");
        return Task.CompletedTask;
    }

    public static Task FsRepairUseBeforeDef()
    {
        string src = string.Join("\n", new[]
        {
            "module Program", "", "open System", "",
            "let rec hanoi n source dest =",
            "    if n = 1 then [ (source, dest) ]",
            "    else",
            "        let movimentosAux = hanoi n source dest_aux",
            "        let movimentosDest = hanoi n dest_aux source",
            "        movimentosAux @ [ (source, dest) ] @ movimentosDest",
            "",
            "let dest_aux = 3"
        });
        string err = "/tmp/p/Program.fs(8,44): error FS0039: The value or constructor 'dest_aux' is not defined. "
                   + "Maybe you want one of the following: dest";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("'dest_aux' e' usado na linha 8") && p.Contains("declarado na linha 12"),
            "FS0039 uso antes da declaracao -> dica com as duas linhas (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let auxiliar = 3\n\nlet rec soma n ="), "a dica mostra a forma que compila");
        TestContext.Check(!p.Contains("dest_aux = 3\n"), "a dica nao manda copiar o bloco errado");

        string ok = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let dest_aux = 3\n\nlet rec f n = if n = 0 then dest_aux else f (n - 1)\n",
            "/tmp/p/Program.fs(3,20): error FS0039: The value or constructor 'outro' is not defined.");
        TestContext.Check(ok.Length == 0,
            "nome sem declaracao no fonte -> silencio (veio: " + ok.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    public static Task FsRepairIfBranchesString()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let hanoi_seq n =\n    if n <= 0 then \"\"\n    else\n        let subSeq = \"1->2\"\n        subSeq @ \",\" + \"1->3\"\n",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is 'string'. This branch returns a value of type ''a list'.");
        TestContext.Check(p.Contains("TEXTO com `+`") && p.Contains("LISTA com `@`"),
            "FS0001 ramo string -> dica ensina + para texto e @ para lista (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let texto = a + \",\" + b"), "a dica mostra a forma que compila");
        TestContext.Check(!p.Contains("Array.append antes depois"), "ramo string nao recebe a dica de array");

        string q = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let f xs = if xs = [] then [] else xs\n",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is ''a list'. This branch returns a value of type 'int'.");
        TestContext.Check(q.Contains("para juntar LISTA use `@`"),
            "ramo lista -> dica de `@` (veio: " + q.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    public static Task FsRepairListContainsOrder()
    {
        const string src = "type Node = { Keys: int list }\nlet node = { Keys = [1; 2] }\nlet key = 2\nlet ok = List.contains node.Keys key\n";
        const string err = "Program.fs(4,34): error FS0001: This expression was expected to have type\n    'int list list'    \nbut here has type\n    'int'";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("List.contains key node.Keys"),
            "FS0001 'list list' -> valor antes, lista depois (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("linha 4"),
            "a dica aponta a linha do erro (veio: " + p.Replace("\n", " | ") + ")");
        // Mesmo tipo esperado SEM `List.contains` na linha: sem medida, sem dica.
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let total xs = List.sum xs\n",
            "Program.fs(1,16): error FS0001: This expression was expected to have type\n    'int list list'    \nbut here has type\n    'int'") == "",
            "'list list' sem List.contains nao recebe a dica de ordem");
        // `List.contains` com a ordem CERTA nao gera esse erro: a dica de ORDEM nao entra
        // (outra regra pode responder ao proprio erro — o pin olha a dica de ordem).
        string outro = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let ok = List.contains 2 [1; 2]\n",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is 'string'. This branch returns a value of type 'int'.");
        TestContext.Check(!outro.Contains("List.contains key node.Keys"),
            "erro de ramos do if nao vira dica de ordem de List.contains (veio: " + outro.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    public static Task FsRepairEqualityOperator()
    {
        const string src = "let rec search (node: Node option) (value: int) : bool =\n    match node with\n    | Some n -> if value == n.Value then true else false\n    | None -> false\n";
        const string err = "Program.fs(3,22): error FS0043: The type 'int' does not support the operator '=='";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("if value = node.Value then true"),
            "FS0043 '==' -> dica ensina `=` (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("linha 3"),
            "a dica aponta a linha do erro (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("=="),
            "a dica nao repete o operador que nao existe em F# (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let x = 1\n", "error FS0001: This expression was expected to have type 'int' but here has type 'string'") == "",
            "erro alheio nao recebe a dica de igualdade");
        return Task.CompletedTask;
    }

    public static Task FsRepairPowerOperator()
    {
        const string src = "let distanceSq (p: (int * int)) (c: (float * float)) : float =\n    (fst p - fst c) ^ 2 + (snd p - snd c) ^ 2\n";
        const string err = "Program.fs(2,5): error FS0101: This construct is deprecated. This construct is deprecated. Use the '+' operator instead.";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("** 2.0") && p.Contains("pown"),
            "FS0101 `^` -> `**` float e `pown` inteiro (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("^ 2"),
            "a dica nao repete o operador obsoleto como exemplo");
        // FS0101 sem `^ <numero>` na linha do erro: outro construto obsoleto, silencio.
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "#light \"off\"\nlet x = 1\n", err) == "",
            "FS0101 sem potencia na linha nao recebe a dica de `**`");
        return Task.CompletedTask;
    }

    public static Task FsRepairParamSemicolon()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec hanoi (n: int; source: string; destination: string; auxiliary: string) =\n    if n = 0 then [] else []\n",
            "error FS0010: Unexpected symbol ';' in pattern. Expected ')' or other token.");
        TestContext.Check(p.Contains("(n: int) (source: int)"),
            "FS0010 ';' -> dica ensina parametros curried (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("(n: int;"), "a dica nao ecoa a lista de parametros com `;`");
        return Task.CompletedTask;
    }

    public static Task FsRepairGenericSyntax()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let mutable sequencia = []<string list>\n",
            "/tmp/x/Program.fs(34,43): error FS3156: Unexpected token '>' or incomplete expression");
        TestContext.Check(p.Contains("let vazia: string list = []"),
            "FS3156 generico -> anotacao pos-fixada com `:` (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("[]<string list>"), "a dica nao ecoa o generico que nao compila");
        TestContext.Check(p.Contains("linha 34"), "a dica aponta a linha do erro (o modelo editava outra)");
        string seq = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let mutable sequencia = []\nsequencia <- seq {+ source, dest}\n",
            "/tmp/x/Program.fs(37,24): error FS0010: Unexpected symbol '+' in expression");
        TestContext.Check(seq.Contains("yield") && seq.Contains("@ [ n ]"),
            "FS0010 `seq {+` -> comprehension exige yield; acumular e' lista com `@` (veio: " + seq.Replace("\n", " | ") + ")");
        TestContext.Check(!seq.Contains("seq {+"), "a dica nao ecoa a comprehension invalida");
        // 2026-10-30 (kmeans): `list<Point>` (generico LEGITIMO no fonte) nao pode
        // puxar a dica de `<...>` quando o erro aponta OUTRA linha — a dica errada
        // saiu 4 vezes e o modelo reescrevia o arquivo inteiro com a mesma falha.
        string outroErro = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let a (xs: list<Point>) = xs\nlet ps = [| { X = 1.0; Y = 1.0 } |]\n",
            "/tmp/x/Program.fs(2,44): error FS0010: Unexpected symbol '|]' in expression. Expected '}' or other token.");
        TestContext.Check(!outroErro.Contains("let vazia: string list"),
            "generico no fonte fora da linha do erro nao vira dica de `<...>` (veio: " + outroErro.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    public static Task FsRepairRecordArrayClose()
    {
        const string src = "type Point = { X: float; Y: float }\nlet points = [| { X = 1.0; Y = 1.0 }; { X = 9.0; Y = 8.0 |]\n";
        const string err = "Program.fs(2,59): error FS0010: Unexpected symbol '|]' in expression. Expected '}' or other token.";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("{ X = 1.0; Y = 2.0 } |]"),
            "FS0010 `|]` -> cada registro fecha com `}` antes do `|]` (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("linha 2"), "a dica aponta a linha do erro");
        // Outro FS0010 (nao o `|]`) segue sem esta dica.
        TestContext.Check(global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let xs = [ 1 2 ]\n", "Program.fs(1,12): error FS0010: Unexpected integer literal in binding") == "",
            "FS0010 sem `|]` nao recebe a dica de registro");
        return Task.CompletedTask;
    }

    public static Task FsRepairInterpolatedString()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rotulo = String.concat \",\" sequencia\nprintfn $\"hanoi_seq={String.concat \",\" sequencia}\"\n",
            "/tmp/x/Program.fs(21,40): error FS3373: Invalid interpolated string. Single quote or verbatim string literals may not be used in interpolated expressions in single quote or verbatim strings.");
        TestContext.Check(p.Contains("printfn \"hanoi_seq=%s\" rotulo"),
            "FS3373 -> dica ensina formato posicional com `let` (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("String.concat \",\" sequencia}\""),
            "a dica nao ecoa a interpolacao que nao compila");
        TestContext.Check(p.Contains("linha 21"), "a dica aponta a linha do erro");
        return Task.CompletedTask;
    }

    public static Task FsRepairErrorLinePointer()
    {
        const string err = "/tmp/x/Program.fs(12,5): error FS0010: Unexpected symbol '->' in expression.";
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let xs = [ a -> b ]\n", err);
        TestContext.Check(p.Contains("linha 12"), "a dica nomeia a linha do compilador (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Split("linha 12").Length - 1 == 1, "a linha aparece UMA vez na dica");
        string semLinha = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let xs = [ a -> b ]\n", "error FS0010: Unexpected symbol '->' in expression.");
        TestContext.Check(!semLinha.Contains("O erro esta na linha"),
            "sem (linha,coluna) na saida nao inventa ponteiro");
        return Task.CompletedTask;
    }

    public static Task FsRepairIndexerAnnotation()
    {
        string p = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(
            "let rec sieveRec p currentPrimes =\n    if currentPrimes.[p] then sieveRec (p + 1) currentPrimes else currentPrimes\n",
            "error FS0752: The operator 'expr.[idx]' has been used on an object of indeterminate type based on information prior to this program point. Consider adding further type constraints");
        TestContext.Check(p.Contains("(xs: bool array)") && p.Contains("xs.[i]"),
            "FS0752 -> dica anota o parametro indexado (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("currentPrimes.[p]"), "a dica nao ecoa a linha sem anotacao");
        return Task.CompletedTask;
    }
}
