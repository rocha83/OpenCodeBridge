using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace adcs
{
    class Program
    {
        static void Main(string[] args)
        {
            var stopwatch = Stopwatch.StartNew();

            // 1. Simpson Adaptativo de sin[0,pi]
            var simpsonResult = SimpsonAdaptive(Math.Sin, 0, Math.PI, 1e-6);
            var simpsonValue = simpsonResult.Item1;
            var simpsonSteps = simpsonResult.Item2;

            // 2. Monte Carlo de pi com 200k pontos
            var mcResult = MonteCarloPi(200_000, 42);
            var mcValue = mcResult.Item1;
            var mcPoints = mcResult.Item2;

            // 3. Estatísticas genéricas
            var stats = GenerateStats(Enumerable.Range(1, 100).Select(i => Math.Sin(i * Math.PI / 180.0)));

            // 4. Regressão linear
            var xValues = Enumerable.Range(1, 10).Select(i => (double)i);
            var yValues = xValues.Select(x => 2.0 * x + 1.0 + (Math.Sin(x) * 0.1));
            var regression = LinearRegression(xValues, yValues);

            // 5. Números para imprimir
            var numbers = new List<double>
            {
                simpsonValue,
                mcValue,
                stats.mean,
                stats.stdDev,
                regression.r2
            };

            // Imprimir com 6 casas decimais
            Console.WriteLine("Resultados:");
            for (int i = 0; i < numbers.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {numbers[i]:F6}");
            }

            stopwatch.Stop();
            Console.WriteLine($"\nTempo total: {stopwatch.ElapsedMilliseconds}ms");
        }

        // Simpson Adaptativo
        static (double value, int steps) SimpsonAdaptive(Func<double, double> f, double a, double b, double tolerance)
        {
            double h = b - a;
            double c = (a + b) / 2.0;
            double fa = f(a);
            double fb = f(b);
            double fc = f(c);
            double S1 = (h / 6.0) * (fa + 4.0 * fc + fb);

            double d = (a + c) / 2.0;
            double e = (c + b) / 2.0;
            double fd = f(d);
            double fe = f(e);
            double S2 = (h / 12.0) * (fa + 4.0 * fd + 2.0 * fc + 4.0 * fe + fb);

            if (Math.Abs(S2 - S1) < tolerance)
            {
                return (S2, 1);
            }

            var left = SimpsonAdaptive(f, a, d, tolerance / 2);
            var right1 = SimpsonAdaptive(f, d, c, tolerance / 2);
            var right2 = SimpsonAdaptive(f, c, e, tolerance / 2);
            var right3 = SimpsonAdaptive(f, e, b, tolerance / 2);

            return (left.Item1 + right1.Item1 + right2.Item1 + right3.Item1, 
                    left.Item2 + right1.Item2 + right2.Item2 + right3.Item2);
        }

        // Monte Carlo para pi
        static (double pi, int points) MonteCarloPi(int points, int seed)
        {
            var random = new Random(seed);
            int inside = 0;

            for (int i = 0; i < points; i++)
            {
                double x = random.NextDouble();
                double y = random.NextDouble();
                if (x * x + y * y <= 1.0)
                {
                    inside++;
                }
            }

            return (4.0 * inside / points, points);
        }

        // Estatísticas genéricas
        static (double mean, double stdDev) GenerateStats(IEnumerable<double> values)
        {
            var list = values.ToList();
            int n = list.Count;
            if (n == 0) return (0, 0);

            double sum = list.Sum();
            double mean = sum / n;

            double sumSquaredDiff = list.Sum(x => Math.Pow(x - mean, 2));
            double variance = sumSquaredDiff / (n - 1); // Amostragem
            double stdDev = Math.Sqrt(variance);

            return (mean, stdDev);
        }

        // Regressão linear
        static (double a, double b, double r2) LinearRegression(IEnumerable<double> x, IEnumerable<double> y)
        {
            var xList = x.ToList();
            var yList = y.ToList();
            int n = xList.Count;

            double sumX = xList.Sum();
            double sumY = yList.Sum();
            double sumXY = xList.Zip(yList, (x, y) => x * y).Sum();
            double sumX2 = xList.Select(x => x * x).Sum();
            double sumY2 = yList.Select(y => y * y).Sum();

            double denom = n * sumX2 - sumX * sumX;
            double slope = (n * sumXY - sumX * sumY) / denom;
            double intercept = (sumY - slope * sumX) / n;

            // Coeficiente de determinação R²
            double meanY = sumY / n;
            double ssTot = yList.Sum(y => Math.Pow(y - meanY, 2));
            double ssRes = xList.Zip(yList, (x, y) =>
            {
                double predicted = slope * x + intercept;
                return Math.Pow(y - predicted, 2);
            }).Sum();

            double r2 = 1.0 - (ssRes / ssTot);

            return (intercept, slope, r2);
        }
    }
}