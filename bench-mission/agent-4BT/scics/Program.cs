using System;
using System.Collections.Generic;
using System.Linq;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=== Integração Numérica de Sin(x) em [0, pi] (Método de Simpson) ===");
        double integralSimpson = SimpsonIntegral();
        Console.WriteLine($"Resultado: {integralSimpson:F6}");

        Console.WriteLine("\n=== Média e Desvio-Padrão de uma Amostra ===");
        int[] amostra = { 2, 4, 4, 4, 5, 5, 7, 9 };
        double media = CalculateAverage(amostra);
        double desvioPadrao = CalculateStandardDeviation(amostra, media);
        Console.WriteLine($"Média: {media:F2}");
        Console.WriteLine($"Desvio-Padrão: {desvioPadrao:F2}");

        Console.WriteLine("\n=== Regressão Linear por Mínimos Quadrados ===");
        List<(double x, double y)> dados = new List<(double, double)>
        {
            (1, 2), (2, 4), (3, 5), (4, 4), (5, 6)
        };
        (double m, double c) = LinearRegression(dados);
        Console.WriteLine($"Equação da reta: y = {m:F4}x + {c:F4}");

        Console.WriteLine("\nPressione qualquer tecla para sair...");
        Console.ReadKey();
    }

    static double SimpsonIntegral()
    {
        const double n = 100; // Número de subintervalos (deve ser par)
        const double a = 0.0;
        const double b = Math.PI;
        double h = (b - a) / n;

        double sum = 0.0;

        // Pontos extremos
        sum += Math.Sin(a) + Math.Sin(b);

        // Pontos ímpares (peso 4)
        for (int i = 1; i < n; i += 2)
        {
            sum += 4 * Math.Sin(a + i * h);
        }

        // Pontos pares (peso 2)
        for (int i = 2; i < n; i += 2)
        {
            sum += 2 * Math.Sin(a + i * h);
        }

        return (h / 6.0) * sum;
    }

    static double CalculateAverage(int[] array)
    {
        return array.Average();
    }

    static double CalculateStandardDeviation(int[] array, double mean)
    {
        double sumSquaredDiff = array.Sum(x => Math.Pow(x - mean, 2));
        return Math.Sqrt(sumSquaredDiff / array.Length);
    }

    static (double m, double c) LinearRegression(List<(double x, double y)> data)
    {
        int n = data.Count;
        double sumX = data.Sum(d => d.x);
        double sumY = data.Sum(d => d.y);
        double sumXY = data.Sum(d => d.x * d.y);
        double sumX2 = data.Sum(d => d.x * d.x);

        double m = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
        double c = (sumY - m * sumX) / n;

        return (m, c);
    }
}