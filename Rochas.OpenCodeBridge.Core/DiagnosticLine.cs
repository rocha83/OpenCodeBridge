using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Ponteiro de LINHA para a dica de sintaxe: acrescenta ao texto já montado o
/// número de linha que o compilador aponta. Genérico de linguagem — o formato
/// <c>arquivo(linha,coluna)</c> vale para o Roslyn (C#) e para o compilador F#.
/// </summary>
/// <remarks>
/// Nasceu no reparo F# e ficou no Core quando a trilha F# foi ARQUIVADA
/// (2026-10-30): o caminho C# depende dele (<see cref="CsSyntaxRepair.Suggest"/>),
/// então ele não é sintaxe F#, é mecânica de diagnóstico compartilhada. Manter uma
/// segunda cópia em <c>CsSyntaxRepair</c> faria as duas divergirem na primeira
/// edição (regra DRY do projeto).
/// </remarks>
public static class DiagnosticLine
{
    /// <summary>
    /// Acrescenta a LINHA que o compilador aponta, quando a dica ainda não a cita.
    /// Medido na 17ª amostra do isolado de 2026-10-29: o erro estava na linha 34 e o
    /// modelo editou a linha 48 seis vezes — a dica ensinava a forma, mas não dizia
    /// ONDE. Aponta o número, nunca o literal errado (o modelo copia o que vê).
    /// </summary>
    /// <param name="hint">Dica já montada.</param>
    /// <param name="compilerError">Saída do build com <c>arquivo(linha,coluna)</c>.</param>
    /// <returns>Dica com o ponteiro de linha (ou a própria dica quando não há linha).</returns>
    public static string WithErrorLine(string hint, string compilerError)
    {
        var where = Regex.Match(compilerError, @"\((\d+),(\d+)\)");
        if (!where.Success) return hint;
        string line = where.Groups[1].Value;
        if (Regex.IsMatch(hint, @"linha " + line + @"\b")) return hint;
        return hint + "\n\nO erro esta na linha " + line
             + " do arquivo: edite ESSA linha (o compilador aponta o lugar exato).";
    }
}
