namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IHarnessLinter
{
    /// <summary>
    /// Retorna a lista de hardcodes de infraestrutura detectados no texto do harness.
    /// Lista vazia = texto aprovado.
    /// </summary>
    IReadOnlyList<string> FindHardcodes(string content);
}
