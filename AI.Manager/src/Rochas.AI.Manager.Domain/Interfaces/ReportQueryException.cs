namespace Rochas.AI.Manager.Domain.Interfaces;

/// <summary>
/// Erro de validacao de relatorio (entidade, eixo temporal ou filtro nao suportado).
/// Deve ser traduzido para HTTP 400 pelos controllers.
/// </summary>
public sealed class ReportQueryException : Exception
{
    public ReportQueryException(string message) : base(message) { }
}
