// scripts/diag/fs-repair-probe.fsx
// ---------------------------------------------------------------------------
// Prova empirica das formas que o FsSyntaxRepair (Core) passa a emitir.
// Rodar:  dotnet fsi scripts/diag/fs-repair-probe.fsx
//
// Contexto (L3 de 2026-10-29, step mission-tools-bridge): o reparo devolvia
// `[for i in 1 .. n - 1 do i -> ...]`, que NAO compila, e o modelo copia o
// literal da dica. As formas abaixo sao as que o CERTO usa hoje.
//
// REGRA que este script documenta: `do` dentro de LISTA sem `yield` e' dependente
// da inferencia — vale com tipo concreto/anotado e quebra quando o elemento ainda
// e' variavel de tipo (FS0001 'unit' does not support '+'). `->` e `do yield`
// declaram o elemento e sao estaveis nos dois contextos.
// ---------------------------------------------------------------------------
open System

let check label ok =
    printfn "%-42s %s" label (if ok then "OK" else "FALHOU")

// 1) arrow no lugar de `do`, com `f` parametrico (o caso que falhou no L3)
let simpson n a b f =
    let h = (b - a) / float n
    let sumOdd = List.sum [for i in 1 .. 2 .. n - 1 -> f (a + float i * h)]
    let sumEven = List.sum [for i in 2 .. 2 .. n - 1 -> f (a + float i * h)]
    (h / 3.0) * (f a + f b + 4.0 * sumOdd + 2.0 * sumEven)

let simpsonValue = simpson 100 0.0 Math.PI Math.Sin
printfn "simpson arrow+passo (meta 2.000000)      %.6f" simpsonValue
check "arrow + passo -> Simpson = 2.000000" (abs (simpsonValue - 2.0) < 1e-4)

// 2) do + yield com filtro (fallback quando a condicao nao e' paridade)
let odds = [for i in 1 .. 5 do if i % 2 <> 0 then yield float i]
check "do yield com filtro -> [1;3;5]" (odds = [1.0; 3.0; 5.0])

// 3) arrow + if/then/else (a forma do `do` puro corrigido)
let mixed = [for i in 1 .. 3 -> if i % 2 <> 0 then float i else 0.0]
check "arrow + if/then/else -> [1;0;3]" (mixed = [1.0; 0.0; 3.0])

// 4) arrow puro (comprehension que vinha com '|' de outra linguagem)
let data = [2.0; 4.0; 5.0; 9.0]
let mean = List.sum data / float (List.length data)
let squared = [for x in data -> (x - mean) ** 2.0]
check "arrow puro -> desvios quadraticos" (abs ((List.sum squared) - 26.0) < 1e-9)

// 5) `do` sem yield com tipo ABERTO quebra (deterministico) — por isso o reparo
//    migra para `->`. Mantido como comentario executavel: se um dia passar, o
//    diagnostico muda e este bloco precisa ser revisto.
//    let quebrada f = [for i in 1 .. 3 do if i % 2 <> 0 then f (float i) else 0.0]
//    -> error FS0001: The type 'unit' does not support the operator '+'

// 6) `open System.Math` e' FS0039: Math e' TIPO, nao namespace.
let qualificado = System.Math.Sin 0.0
check "System.Math.Sin qualificado" (qualificado = 0.0)

// 7) Cabecalho do arquivo (medido no L4 de 2026-10-29, step algo-bridge): `module X`
//    na MESMA linha do `open` nao compila (FS0010 no `let` seguinte) e a mensagem do
//    compilador nao diz a forma certa. O que o FsSyntaxRepair ensina hoje e' o modulo
//    sozinho com cada `open` na propria linha — abaixo a prova, compilando as duas.
let private buildHeader (header: string) =
    let dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fs-repair-probe", string (abs (header.GetHashCode())))
    System.IO.Directory.CreateDirectory dir |> ignore
    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "p.fsproj"),
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>" +
        "<TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
        "<ItemGroup><Compile Include=\"Program.fs\" /></ItemGroup></Project>")
    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "Program.fs"),
        header + "\n\nlet soma a b = a + b\nprintfn \"%d\" (soma 1 2)\n")
    let psi = System.Diagnostics.ProcessStartInfo("dotnet", "build -c Release --nologo -v q",
                                                  WorkingDirectory = dir,
                                                  RedirectStandardOutput = true,
                                                  RedirectStandardError = true)
    use p = System.Diagnostics.Process.Start psi
    p.WaitForExit()
    p.ExitCode

check "cabecalho colado -> compilador recusa" (buildHeader "module Program open System" <> 0)
check "cabecalho ensinado (module sozinho + open) -> compila" (buildHeader "module Program\n\nopen System" = 0)
printfn ""
printfn "Se 'do' sem yield com tipo aberto compilar, revise FsSyntaxRepair/HarnessTails."
