using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Reparo de sintaxe C# no retry: dado o erro do compilador e o fonte atual, devolve
/// a forma CERTA do que o modelo erra (bloco sem fechar, instrução sem ';', parêntese
/// aberto). Genérico de propósito: não conhece o algoritmo do pedido, só a forma da
/// linguagem. Único lugar dessa mecânica em C# — o teste consome.
/// </summary>
/// <remarks>
/// Nasceu do L4 de 2026-10-30: `algo-cs-btree-bridge` FAIL em 345s com
/// <c>Program.cs(101,6): error CS1513: } expected</c> — o modelo escreveu um arquivo
/// com 26 abre-chave e 25 fecha-chave e gastou os 26 turnos sem fechar o último bloco.
/// A trilha F# tinha reparo in-flow (<see cref="FsSyntaxRepair"/>); a C#, que é a
/// válida desde o arquivamento de 2026-10-30, não tinha nenhum.
/// </remarks>
public static class CsSyntaxRepair
{
    /// <summary>
    /// Sugestão concreta para o erro. Vazio quando não há mapa conhecido — nesse caso
    /// o chamador mantém só a linha do compilador (sem inventar correção).
    /// </summary>
    /// <param name="source">Fonte C# atual (texto completo).</param>
    /// <param name="compilerError">Saída do build com o diagnóstico CSxxxx.</param>
    /// <param name="repeatCount">Quantas vezes ESTA MESMA dica já foi entregue antes
    /// (0 na primeira). A partir da 3ª o Core manda reescrever o arquivo inteiro —
    /// dica idêntica repetida não ensina (medido em 2026-10-29 na trilha F#).</param>
    /// <returns>"ERRO: ...\nCERTO: ..." ou string vazia.</returns>
    public static string Suggest(string source, string compilerError, int repeatCount = 0)
    {
        string hint = SuggestForCode(source, compilerError);
        if (hint.Length == 0) return hint;
        hint = FsSyntaxRepair.WithErrorLine(hint, compilerError);
        if (repeatCount < 2) return hint;
        return hint + "\n\nA MESMA dica ja' veio " + (repeatCount + 1)
             + " vezes: pare de editar a mesma linha e reescreva o arquivo inteiro com a tool `write`, aplicando a forma acima.";
    }

    /// <summary>
    /// Há diagnóstico C# com código na saída do build? Vale para `error` e `warning`
    /// (o CS0168/CS0219 saem como warning e o reparo só de `error` não dispararia).
    /// </summary>
    /// <param name="output">Saída do build/execução.</param>
    /// <returns>True quando existe um código CSxxxx na saída.</returns>
    public static bool HasDiagnostic(string output)
        => !string.IsNullOrEmpty(output)
           && Regex.IsMatch(output, @"(?:error|warning) CS\d{4}");

    static string SuggestForCode(string source, string compilerError)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(compilerError)) return "";
        // Despacha pelo PRIMEIRO diagnóstico: o `dotnet` devolve vários e decidir pelo
        // blob fazia a dica de um código responder a outro (medido em 2026-10-29).
        switch (FirstCode(compilerError))
        {
            case "CS1513": return FixMissingBrace(source);
            case "CS1026": return "ERRO: parêntese aberto sem `)`.\n"
                                + "CERTO: feche o par na MESMA linha que o compilador aponta.";
            case "CS1002": return "ERRO: instrução sem `;`.\n"
                                + "CERTO: termine a instrução da linha apontada com `;`.";
            default: return "";
        }
    }

    /// <summary>
    /// CS1513 (<c>} expected</c>): diz QUANTOS blocos faltam fechar, com a contagem
    /// real do arquivo. A forma ensinada é sempre válida (linhas com `}` no fim), e a
    /// dica nunca mostra um literal inválido copiável.
    /// </summary>
    /// <param name="source">Fonte C# atual.</param>
    /// <returns>Dica com a contagem, ou "" quando não há desbalanceamento simples.</returns>
    static string FixMissingBrace(string source)
    {
        int open = 0, close = 0;
        foreach (char c in source)
        {
            if (c == '{') open++;
            else if (c == '}') close++;
        }
        if (open <= close) return "";
        int missing = open - close;
        return "ERRO: " + missing + " bloco(s) aberto(s) sem fechar — o arquivo tem "
             + open + " abre-chave e " + close + " fecha-chave.\n"
             + "CERTO: acrescente " + missing + " linha(s) contendo só `}` no fim do arquivo, "
             + "uma para cada bloco que ficou aberto (a última fecha a declaração mais externa, "
             + "namespace ou classe).";
    }

    static string FirstCode(string compilerError)
    {
        var m = Regex.Match(compilerError, @"error (CS\d{4})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(compilerError, @"warning (CS\d{4})");
        return m.Success ? m.Groups[1].Value : "";
    }
}
