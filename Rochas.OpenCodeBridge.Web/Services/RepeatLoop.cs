// Trava anti-repeticao do lado Web: faz o MODELO parar (aviso de contencao
// no input + retry do turno), nao apenas conteve o recebimento.
// Espelha o detector da bridge (verbatim 128 + 3 blocos similares), mesmos
// limiares p/ o comportamento ser identico nos dois lados.
namespace Rochas.OpenCodeBridge.Web.Services;

internal static class RepeatLoop
{
    // Aviso de contencao: devolvido ao modelo como mensagem user no retry.
    internal const string Notice = "Aqui e a ponte de contencao OpenCodeBridge pare de enviar o mesmo raciocinio, provavelmente voce entrou em repeticao invalida, mude a sua deducao para outra alternativa e responda, obrigado.";

    internal const int Window = 128;

    /// <summary>Repeticao invalida no texto acumulado? (verbatim ou 3 blocos similares).</summary>
    internal static bool Trips(string? accumulated)
    {
        if (string.IsNullOrEmpty(accumulated)) return false;
        string norm = Norm(accumulated);
        // Cauda verbatim (loops longos exatos).
        if (norm.Length >= 2 * Window)
        {
            string tail = norm[^Window..];
            if (norm.AsSpan(0, norm.Length - Window).IndexOf(tail.AsSpan(), StringComparison.Ordinal) >= 0)
                return true;
        }
        // Loop curto com variacao: 3 blocos finais consecutivos similares.
        foreach (int u in new[] { 24, 32, 40, 48, 64, 80, 96, 128 })
        {
            if (norm.Length < 3 * u) continue;
            var a = norm.AsSpan(norm.Length - u);
            var b = norm.AsSpan(norm.Length - 2 * u, u);
            var c = norm.AsSpan(norm.Length - 3 * u, u);
            if (Sim(a, b) >= 0.85 && Sim(b, c) >= 0.85) return true;
        }
        return false;
    }

    private static string Norm(string s)
    {
        var b = new System.Text.StringBuilder(s.Length);
        bool sp = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { if (!sp) { b.Append(' '); sp = true; } }
            else { b.Append(char.ToLowerInvariant(c)); sp = false; }
        }
        return b.ToString();
    }

    private static double Sim(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return 0;
        Span<int> prev = stackalloc int[m + 1];
        Span<int> cur = stackalloc int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
                cur[j] = System.Math.Min(prev[j] + 1, System.Math.Min(cur[j - 1] + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            var t = prev; prev = cur; cur = t;
        }
        return 1.0 - (double)prev[m] / System.Math.Max(n, m);
    }
}
