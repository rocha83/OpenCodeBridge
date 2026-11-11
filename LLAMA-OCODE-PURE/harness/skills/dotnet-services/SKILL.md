---
name: dotnet-services
description: "Use quando o pedido envolver serviço de aplicação com regra de negócio: cálculo de valor, faixa de aprovação, validação de CPF/CNPJ, Result<T> e erros de domínio sem HTTP."
---

=== EXEMPLO 3 — serviços de aplicação: cálculo e validação (não copie: leia a dinâmica) ===
A especificação mora no domínio (documento, cálculo); a aplicação só orquestra.

```csharp
// Domain/CpfCnpj.cs — regra de documento, sem I/O
public static class CpfCnpj {
    public static bool IsValid(string value) {
        var d = new string(value.Where(char.IsDigit).ToArray());
        if (d.Length is not (11 or 14) || d.Distinct().Count() == 1) return false;
        var len = d.Length == 11 ? 9 : 12;
        return Check(d, len) && Check(d, len + 1);
    }
    static bool Check(string d, int count) {
        int[] w = d.Length == 11
            ? (count == 9 ? [10, 9, 8, 7, 6, 5, 4, 3, 2] : [11, 10, 9, 8, 7, 6, 5, 4, 3, 2])
            : (count == 12 ? [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2] : [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var sum = 0;
        for (var i = 0; i < count; i++) sum += (d[i] - '0') * w[i];
        var rest = sum % 11;
        return d[count] - '0' == (rest < 2 ? 0 : 11 - rest);
    }
}
// Domain/ICpfCnpjValidator.cs
public interface ICpfCnpjValidator { bool IsValid(string value); }
// Domain/ICalculationService.cs — contrato no domínio
public interface ICalculationService {
    long Factorial(int n);
    int Gcd(int a, int b);
    int CountPrimes(int upTo);
}
// Application/CalculationService.cs — laço, nunca literal de resultado
public sealed class CalculationService : ICalculationService {
    public long Factorial(int n) { var r = 1L; for (var i = 2; i <= n; i++) r *= i; return r; }
    public int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }
    public int CountPrimes(int upTo) {
        var total = 0;
        for (var n = 2; n <= upTo; n++) {
            var prime = true;
            for (var d = 2; d * d <= n && prime; d++) prime = n % d != 0;
            if (prime) total++;
        }
        return total;
    }
}
// Program.cs — saída comparável, um valor por linha
var calc = new CalculationService();
Console.WriteLine(calc.Factorial(10));
Console.WriteLine(calc.Gcd(1071, 462));
Console.WriteLine(calc.CountPrimes(50));
```
