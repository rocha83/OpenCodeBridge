namespace Rochas.AI.Manager.Domain.Interfaces;

public enum AccuracyBand { Low, Medium, High }

public sealed record AccuracyResult(double Score, AccuracyBand Band, int MatchedTokens, int ExpectedTokens, int ActualTokens);

public interface IAccuracyEvaluator
{
    AccuracyResult Evaluate(string? expected, string? actual);
}
