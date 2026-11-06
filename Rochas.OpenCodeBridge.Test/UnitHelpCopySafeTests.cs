using System.Text.RegularExpressions;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Exemplo de help tem que ser SEGURO de copiar ao pe' da letra: o 4B copia o
/// literal como se fosse o dado do pedido (2026-11-06: o `notas/hoje.txt` do
/// exemplo foi para o `path` do git e o repo real nunca recebeu commit). Duas
/// defesas medidas juntas: a frase unica <see cref="ToolHelp.ExampleRule"/> na
/// cauda e nenhum exemplo com pasta inventada ou URL que pareca o pedido.
/// </summary>
internal static class UnitHelpCopySafeTests
{
    static readonly Regex PathArg = new("\"path\":\"([^\"]+)\"", RegexOptions.Compiled);

    public static Task ExamplesAreSafeToCopy()
    {
        TestContext.Check(HarnessTails.DefaultTools.Contains(ToolHelp.ExampleRule, StringComparison.Ordinal),
            "a regra dos exemplos entra UMA vez na cauda (fonte unica)");
        TestContext.Check(!MainHarness.Text.Contains("notas", StringComparison.Ordinal),
            "main harness nao cita pasta inventada (o modelo copia)");

        foreach (var (name, doc) in ToolHelp.All)
        {
            foreach (Match m in PathArg.Matches(doc))
            {
                string p = m.Groups[1].Value;
                // "./" e "." valem em qualquer workspace; nome simples e' arquivo
                // da raiz (o exemplo o cria, ou o pedido o nomeia).
                TestContext.Check(p is "./" or "." || !p.Contains('/'),
                    $"{name}: path de exemplo nao aponta pasta inventada ('{p}')");
            }
            TestContext.Check(!doc.Contains("open-meteo", StringComparison.Ordinal),
                $"{name}: exemplo nao sequestra a URL do pedido");
            TestContext.Check(!doc.Contains("notas", StringComparison.Ordinal),
                $"{name}: help sem a pasta inventada que o modelo copiou");
        }
        return Task.CompletedTask;
    }
}
