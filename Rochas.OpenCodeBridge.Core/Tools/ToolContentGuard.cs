using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Trava de conteudo ELIDIDO: `write` e `edit` recusam texto que traz um marcador
/// de continuacao no lugar do corpo. Fonte unica das duas tools — o consumidor
/// nao repete a regra.
/// </summary>
/// <remarks>
/// Motivo medido (2026-10-30, `E-mission-algo-cs-btree`): o modelo mandou
/// `// ... (restante do código)` como `newString` de `edit`; o disco passou a ter
/// o marcador no lugar do corpo, o build quebrou e o turno seguinte gastou em
/// "oldString não encontrado" — 13 turnos sem artefato. A recusa devolve a forma
/// que funciona (o texto inteiro), sem exibir o marcador proibido.
/// </remarks>
public static class ToolContentGuard
{
    private static readonly Regex Marker = new(
        @"//\s*\.\.\.|/\*\s*\.\.\.|\(\s*restante\b|\bresto igual\b|\bmesmo c[oó]digo\b|\bomitted\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Mensagem de recusa quando o conteudo traz trecho elidido, ou <c>null</c>
    /// quando o conteudo e' o artefato completo.
    /// </summary>
    /// <param name="content">Texto que seria gravado (content do write / newString do edit).</param>
    /// <param name="tool">Nome da tool, para a mensagem.</param>
    /// <returns>Mensagem pronta para o historico, ou <c>null</c>.</returns>
    public static string? Elided(string? content, string tool)
    {
        if (string.IsNullOrEmpty(content)) return null;
        Match m = Marker.Match(content);
        if (!m.Success) return null;
        int line = 1;
        for (int i = 0; i < m.Index && i < content!.Length; i++)
            if (content[i] == '\n') line++;
        return $"{tool}: conteudo com trecho elidido (marcador de continuacao na linha {line} no lugar do corpo). "
             + "O arquivo em disco fica exatamente com o texto enviado: reenvie a chamada com o trecho COMPLETO, "
             + "linha por linha, escrevendo cada declaracao por inteiro.";
    }

    /// <summary>
    /// `edit` com o ARQUIVO INTEIRO no <c>newString</c> e uma linha curta no
    /// <c>oldString</c>: e' reescrita total disfarcada de edicao. Aplicar duplica o
    /// cabecalho/declaracao e o build quebra (CS1022; medido 2026-10-30 no
    /// <c>E-mission-algo-cs-sort</c>, 6 turnos em loop). A recusa manda a tool certa.
    /// </summary>
    /// <param name="oldString">Trecho a substituir.</param>
    /// <param name="newString">Texto novo.</param>
    /// <returns>Mensagem pronta para o historico, ou <c>null</c>.</returns>
    public static string? WholeFileRewrite(string? oldString, string? newString)
    {
        if (string.IsNullOrEmpty(oldString) || string.IsNullOrEmpty(newString)) return null;
        // So' quando o oldString e' UM trecho curto (1 linha): um bloco grande no
        // oldString e' edicao de bloco legitima.
        if (oldString.Contains('\n') || oldString.Trim().Length > 40) return null;
        if (newString.Count(c => c == '\n') < 6) return null;
        bool fileShape = Regex.IsMatch(newString,
            @"(?:^|\n)\s*(?:using\s+[\w.]+;|namespace\s+[\w.]+|(?:public|internal|static|sealed|partial)?\s*class\s+\w+|module\s+\w+)");
        if (!fileShape) return null;
        return "edit: o `newString` traz o ARQUIVO INTEIRO (varias declaracoes) e o `oldString` e' uma linha curta. "
             + "`edit` substitui um TRECHO, nao regrava o arquivo: para o arquivo completo use `write` "
             + "(content com o texto inteiro) ou refaca o `edit` com o trecho exato a trocar.";
    }
}
