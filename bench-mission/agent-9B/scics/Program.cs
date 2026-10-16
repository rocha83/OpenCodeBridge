using System;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        // 1. Integração de Simpson de sin(x) em [0, pi]
        double integralSimpson = SimpsonIntegral(Math.Sin, 0, Math.PI, 100);
        Console.WriteLine($"Integral de Simpson de sin(x) em [0, pi]: {integralSimpson:F6}");

        // 2. Média e desvio-padrão da amostra {2,4,4,4,5,5,7,9}
        int[] amostra = { 2, 4, 4, 4, 5, 5, 7, 9 };
        double media = CalcularMedia(amostra);
        double desvioPadrao = CalcularDesvioPadrao(amostra, media);
        Console.WriteLine($"Média da amostra: {media:F6}");
        Console.WriteLine($"Desvio-padrão da amostra: {desvioPadrao:F6}");

        // 3. Regressão linear por mínimos quadrados de [(1,2),(2,4),(3,5),(4,4),(5,6)]
        var pontos = new List<(int x, int y)>
        {
            (1, 2), (2, 4), (3, 5), (4, 4), (5, 6)
        };
        (double a, double b) = RegressaoLinear(pontos);
        Console.WriteLine($"Regressão linear: y = {a:F6}x + {b:F6}");
    }

    static double SimpsonIntegral(Func<double, double> f, double a, double b, int n)
    {
        if (n % 2 != 0) n++; // Simpson requer número par de subintervalos
        double h = (b - a) / n;
        double soma = f(a) + f(b);

        for (int i = 1; i < n; i++)
        {
            double x = a + i * h;
            if (i % 2 == 0)
                soma += 2 * f(x);
            else
                soma += 4 * f(x);
        }

        return soma * h / 3;
    }

    static double CalcularMedia(int[] dados)
    {
        double soma = 0;
        foreach (var d in dados) soma += d;
        return soma / dados.Length;
    }

    static double CalcularDesvioPadrao(int[] dados, double media)
    {
        double somaQuadrados = 0;
        foreach (var d in dados)
            somaQuadrados += Math.Pow(d - media, 2);

        return Math.Sqrt(somaQuadrados / (dados.Length - 1));
    }

    static (double a, double b) RegressaoLinear(List<(int x, int y)> pontos)
    {
        int n = pontos.Count;
        double somaX = 0, somaY = 0, somaXY = 0, somaX2 = 0;

        foreach (var p in pontos)
        {
            somaX += p.x;
            somaY += p.y;
            somaXY += p.x * p.y;
            somaX2 += p.x * p.x;
        }

        double a = (n * somaXY - somaX * somaY) / (n * somaX2 - Math.Pow(somaX, 2));
        double b = (somaY - a * somaX) / n;

        return (a, b);
    }
}