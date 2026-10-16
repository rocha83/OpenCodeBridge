open System

// 1. Integração de Simpson de sin(x) em [0, pi]
let integralSimpson (f: float -> float) (a: float) (b: float) (n: int) : float =
    if n % 2 <> 0 then n <- n + 1 // Simpson requer número par de subintervalos
    let h = (b - a) / float n
    let soma = f a + f b
    let rec loop i currentSoma =
        if i >= n then currentSoma
        else
            let x = a + float i * h
            let termo = if i % 2 = 0 then 2.0 * f x else 4.0 * f x
            loop (i + 1) (currentSoma + termo)
    let resultado = loop 1 soma
    resultado * h / 3.0

let integralSin = integralSimpson sin 0.0f Math.PI 100
printfn $"Integral de Simpson de sin(x) em [0, pi]: {integralSin:F6}"

// 2. Média e desvio-padrão da amostra {2,4,4,4,5,5,7,9}
let amostra = [|2; 4; 4; 4; 5; 5; 7; 9|]
let n = amostra.Length
let media = amostra |> Array.sum / float n
let somaQuadrados = amostra |> Array.map (fun x -> float (x - int media) ** 2.0) |> Array.sum
let desvioPadrao = sqrt (somaQuadrados / float (n - 1))
printfn $"Média da amostra: {media:F6}"
printfn $"Desvio-padrão da amostra: {desvioPadrao:F6}"

// 3. Regressão linear por mínimos quadrados de [(1,2),(2,4),(3,5),(4,4),(5,6)]
let pontos = [(1, 2); (2, 4); (3, 5); (4, 4); (5, 6)]
let n = pontos.Length
let somaX = pontos |> Array.map fst |> Array.sum
let somaY = pontos |> Array.map snd |> Array.sum
let somaXY = pontos |> Array.map (fun (x, y) -> float x * float y) |> Array.sum
let somaX2 = pontos |> Array.map (fun (x, _) -> float x ** 2.0) |> Array.sum

let a = (float n * somaXY - float somaX * float somaY) / (float n * somaX2 - float (somaX ** 2))
let b = (float somaY - a * float somaX) / float n

printfn $"Regressão linear: y = {a:F6}x + {b:F6}"
