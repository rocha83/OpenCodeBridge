using System;
using System.Collections.Generic;
using System.Linq;

namespace scics
{
    class Program
    {
        static void Main(string[] args)
        {
            // 1. Integração de Simpson de sin(x) em [0, pi]
            double integralSimpson = SimpsonIntegral(Math.Sin, 0, Math.PI, 100);
            Console.WriteLine($"Integral de Simpson de sin(x) em [0, pi]: {integralSimpson:F6}");

            // 2. Média e desvio-padrão da amostra {2,4,4,4,5,5,7,9}
            var amostra = new List<double> { 2, 4, 4, 4, 5, 5, 7, 9 };
            double media = Media(amostra);
            double desvioPadrao = DesvioPadrao(amostra);
            Console.WriteLine($"Média da amostra: {media:F6}");
            Console.WriteLine($"Desvio-padrão da amostra: {desvioPadrao:F6}");

            // 3. Regressão linear por mínimos quadrados de [(1,2),(2,4),(3,5),(4,4),(5,6)]
            var pontos = new List<(double x, double y)> 
            { 
                (1, 2), (2, 4), (3, 5), (4, 4), (5, 6) 
            };
            var regressao = RegressaoLinearMimosQuadrados(pontos);
            Console.WriteLine($"Regressão linear: y = {regressao.coeficienteB:F4}x + {regressao.coeficienteA:F4}");
            Console.WriteLine($"Coeficiente de determinação (R²): {regressao.rSquared:F6}");
        }

        // Integração numérica usando a regra de Simpson
        static double SimpsonIntegral(Func<double, double> f, double a, double b, int n)
        {
            if (n % 2 != 0) n++; // Simpson requer número par de intervalos
            
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
            
            return (h / 3) * soma;
        }

        // Média de uma lista
        static double Media(List<double> lista)
        {
            if (lista.Count == 0) return 0;
            double soma = 0;
            foreach (var item in lista) soma += item;
            return soma / lista.Count;
        }

        // Desvio-padrão amostral
        static double DesvioPadrao(List<double> lista)
        {
            if (lista.Count < 2) return 0;
            double media = Media(lista);
            double somaQuadrados = 0;
            foreach (var item in lista)
            {
                double diff = item - media;
                somaQuadrados += diff * diff;
            }
            return Math.Sqrt(somaQuadrados / (lista.Count - 1));
        }

        // Regressão linear por mínimos quadrados
        static (double coeficienteA, double coeficienteB, double rSquared) RegressaoLinearMimosQuadrados(List<(double x, double y)> pontos)
        {
            int n = pontos.Count;
            double somaX = 0, somaY = 0, somaX2 = 0, somaXY = 0;
            
            foreach (var p in pontos)
            {
                somaX += p.x;
                somaY += p.y;
                somaX2 += p.x * p.x;
                somaXY += p.x * p.y;
            }
            
            double denominador = n * somaX2 - somaX * somaX;
            double coeficienteB = (n * somaXY - somaX * somaY) / denominador;
            double coeficienteA = (somaY - coeficienteB * somaX) / n;
            
            // Calcular R²
            double mediaY = somaY / n;
            double somaQuadradosTotal = 0;
            double somaQuadradosResiduo = 0;
            
            foreach (var p in pontos)
            {
                double yPrevisto = coeficienteA + coeficienteB * p.x;
                somaQuadradosTotal += (p.y - mediaY) * (p.y - mediaY);
                somaQuadradosResiduo += (p.y - yPrevisto) * (p.y - yPrevisto);
            }
            
            double rSquared = 1 - (somaQuadradosResiduo / somaQuadradosTotal);
            
            return (coeficienteA, coeficienteB, rSquared);
        }
    }
}