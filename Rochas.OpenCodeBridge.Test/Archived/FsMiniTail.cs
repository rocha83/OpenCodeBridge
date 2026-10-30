namespace Rochas.OpenCodeBridge.Test.Archived;

/// <summary>
/// ARQUIVADO em 2026-10-30 — cauda de sintaxe F# (FsMini), preservada por ser o
/// aprendizado mais valioso e custoso de reproduzir da trilha F#.
/// </summary>
/// <remarks>
/// <para><b>Por que saiu do Core.</b> O FsMini era injetado só na fase F# do
/// teste (`HarnessTails.Compose` não o inclui no default). Com a trilha F#
/// arquivada (ver <see cref="ArchivedFsAlgoMissions"/>), nenhum teste ativo usa
/// a tabela: C# e WAR não recebem sintaxe F#. Mantê-la no Core daria a impressão
/// de que a cauda ainda é contrato de produção.</para>
/// <para><b>Por que guardar.</b> A tabela foi construída com 19 amostras medidas
/// (FS0010, FS0001, FS0039, FS0751, FS3156, FS3373, FS0597, FS0072, FS0193,
/// FS0020, FS3563, FS3118/FS0588/FS0528, FS0603, FS0988, FS0037, FS0058). Ela
/// ensina a forma CERTA (sem exibir o literal inválido que o modelo copia) e é
/// a peça que mais reduziu o loop de compilação. Se a trilha F# voltar, o
/// FsMini volta com ela — sem reescrever a tabela.</para>
/// <para><b>Como reativar.</b> Reintroduzir a const em <c>HarnessTails</c> (ou
/// referenciar <see cref="ArchivedFsMini.FsMini"/>) e injetar no system da fase
/// F#; rodar `U-fsmini-covers-repair-codes` para provar que as formas corretas
/// continuam presentes e os literais inválidos ausentes.</para>
/// </remarks>
public static class ArchivedFsMini
{
    public const string FsMini =
        "F# MINI (sintaxe geral; os exemplos NAO sao o algoritmo do pedido). " +
        "BASE: cada diretiva na propria linha — a 1a linha tem so' `module Program` e o `open` vai na linha de baixo (`open System`); funcao `let f x = x + 1`; saida inteira `printfn \"%d\" v`; entrada `[<EntryPoint>] let main argv = ... 0`. " +
        "LISTA e ARRAY: `[ 1; 2; 3 ]` e' lista e combina com `List.sum`, `List.map (fun x -> x * x)`, `List.filter (fun x -> x > 0)`, `List.fold (fun acc x -> acc + x) 0`, `List.length`, `List.forall`, `List.exists`; `[| 1; 2; 3 |]` e' array de valores fixos e combina com `Array.sum`, `Array.map`, `Array.length`. " +
        "RANGE: `a .. b`; com passo, o PASSO e' o SEGUNDO termo. " +
        "INTEIROS: `%` e' resto; `/` entre dois int trunca. " +
        "IGUALDADE: `=` compara dois valores e `<>` testa a diferenca. " +
        "LAYOUT: o recuo define o bloco; cada expressao do corpo fica alinhada sob o `=`. " +
        "RETORNO: o valor da funcao e' a ULTIMA expressao do corpo (`printfn` so' imprime, nao devolve). " +
        "ACUMULAR: a funcao devolve a lista do ramo e junta com `@` — sem `mutable`, `ref` nem `seq {}`: `let rec passos n = if n = 0 then [] else passos (n - 1) @ [ n ]`; rotulo do movimento `sprintf \"%d->%d\" a b` e a linha inteira `String.concat \",\" movs`. " +
        "RECURSAO: `let rec f n = if n <= 1 then 1 else n * f (n - 1)`. " +
        "AUXILIAR: vive no NIVEL DO MODULO, antes de quem a chama, e recebe por parametro o que usa de fora: `let isPrime n = n > 1 && List.forall (fun d -> n % d <> 0) [2 .. int (Math.Sqrt(float n))]`; no ramo `if/else` vai so' a chamada. " +
        "PRIMOS: divisores candidatos de 2 ate a raiz; o intervalo de ENTRADA pode comecar em 1. " +
        "FATORIAL/PRODUTO: `List.fold (fun acc x -> acc * x) 1 [1..10]`, sem recursao. " +
        "TABELA DE ERROS (codigo: sintoma -> forma correta): " +
        "FS0010 lista -> elemento com `-> expr` ou `do yield expr`: `[for i in 1 .. n-1 -> expr]` ou `[for i in 1 .. n-1 do yield expr]`; lambda e' `fun x -> expr`: `List.fold (fun acc x -> acc + x) 0 xs`; anotacao de retorno leva SO' o tipo, sem `nome:` dentro dos parenteses: `let hanoi (n: int) : int * string list =`; parametros nao levam `;` — separe por ESPACO (curried) ou por VIRGULA dentro de UM parenteses (tupla): `let rec hanoi (n: int) (a: int) (b: int) (c: int) =`. " +
        "FS0751/FS0003 passo -> passo no MEIO dos dois `..`: `[for i in 1 .. 2 .. n-1]` percorre 1, 3, .., n-1; aplicacao -> a chamada e a declaracao usam a MESMA quantidade de parametros: `let rec soma a b c = ...` chamado `soma 1 2 3`. " +
        "FS0001 tipo -> lista com `List.sum [ 1; 2 ]` e array com `Array.sum [| 1; 2 |]`; no range os dois limites sao do MESMO tipo; nos dois ramos do `if` o tipo e' o mesmo e cada tipo tem seu jeito de juntar — TEXTO com `+` (`a + \",\" + b`), LISTA com `@` (`xs @ [ y ]`), ARRAY com `Array.append a b`; nao misture lista com texto. " +
        "FS0752 indice -> o valor indexado precisa de tipo conhecido: anote o parametro ou o binding (`let marca (xs: bool array) i = xs.[i]`, `let zeros: int array = Array.create 10 0`). " +
        "FS3156 tipo -> F# nao tem `<...>`: anote com `:` e o tipo pos-fixado: `let vazia: string list = []`, `let tabela: int array = [| 0; 0 |]`. " +
        "FS0039 nome nao definido -> parametro ou valor ja declarado; a declaracao vem ANTES do primeiro uso (o modulo e' lido de cima para baixo): `let auxiliar = 3` acima de quem o usa, ou receba por parametro; resto de inteiro e' `a % b`; auto-chamada pede `let rec f`. " +
        "FS0037 nome repetido -> uma definicao por nome. " +
        "FS0058 offside -> auxiliar no nivel do modulo; no ramo, so' a chamada. " +
        "FS0988 modulo sem entrada -> a entrada da BASE (`[<EntryPoint>]`). " +
        "FS3118/FS0588/FS0528 corpo -> toda funcao termina numa expressao, e a ultima linha do corpo e' o valor: `let total xs =` / `let dobro = List.map (fun x -> x * 2) xs` / `List.sum dobro` (as linhas do corpo alinhadas sob o `=`); quando o `else` abre ramo com `let`, o `let` vai na linha de baixo, indentado. " +
        "FS0603 array -> a comprehension de array envolve TUDO entre `[|` e `|]`: `[| for i in 1 .. 3 -> i * i |]`; lista de elementos leva `;` e tupla entre parenteses: `[ (1, 2); (1, 3) ]`. " +
        "FS0597 argumento -> quando o argumento e' chamada, pipe ou composicao, ele vai entre parenteses: `printfn \"%s\" (String.concat \",\" movs)`. " +
        "FS0072 tipo -> quando o tipo do parametro nao sai sozinho, anote no lambda: `List.map (fun (a: int, b: int) -> sprintf \"%d->%d\" a b) movs` (ou use `fst`/`snd`). " +
        "FS0193 funcao -> `@` e `List.append` exigem os DOIS operandos: `a @ b` ou `List.append a b` (no pipe, o segundo vem explicito: `x |> List.append ys`). " +
        "FS0020 resultado descartado -> `|> ignore` ou `printfn \"%d\" v`. " +
        "FS3563 identificador invalido -> crase dupla no nome (``mdc``). " +
        "FS3373 texto -> a interpolacao nao aceita aspas dentro da `{...}`: separe num `let` e imprima com formato: `let rotulo = String.concat \",\" movs` / `printfn \"hanoi_seq=%s\" rotulo`. " +
        "SE O BUILD FALHAR: leia a linha `error`, aplique a forma da tabela e edite SO' o trecho; nao reescreva o arquivo igual.";
}
