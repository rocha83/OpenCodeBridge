namespace Rochas.AI.Manager.Application.Services;

using System.Text.RegularExpressions;
using Rochas.AI.Manager.Domain.Interfaces;

/// <summary>
/// Linter de hardcodes de infraestrutura no texto do harness.
/// Regra de ouro: harness descreve comportamento; infraestrutura fica na configuracao.
/// </summary>
public sealed class HarnessLinter : IHarnessLinter
{
    private static readonly (Regex Pattern, string Reason)[] Rules =
    {
        (new Regex(@":\d{4,5}\b", RegexOptions.Compiled), "porta numerica"),
        (new Regex(@"\b\d{3,6}k\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "limite de contexto (ex.: 64k, 160k)"),
        (new Regex(@"\btemp(?:erature)?\s*[:=]\s*\d", RegexOptions.IgnoreCase | RegexOptions.Compiled), "temperatura"),
        (new Regex(@"(/media|/home|/usr|C:\\\\)", RegexOptions.Compiled), "caminho local"),
        (new Regex(@"\b(gemma|qwen|llama|deepseek|openai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "nome de modelo/provider"),
        (new Regex(@"\b\d{4,}\b", RegexOptions.Compiled), "limite numerico grande")
    };

    public IReadOnlyList<string> FindHardcodes(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<string>();

        var findings = new List<string>();
        foreach (var (pattern, reason) in Rules)
        {
            foreach (Match m in pattern.Matches(content))
            {
                findings.Add($"{reason}: '{m.Value}'");
            }
        }
        return findings.Distinct().ToList();
    }
}
