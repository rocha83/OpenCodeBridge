using System.Text.RegularExpressions;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Application.Services;

public sealed class AccuracyEvaluator : IAccuracyEvaluator
{
    public AccuracyResult Evaluate(string? expected, string? actual)
    {
        var expTokens = Tokenize(expected);
        var actTokens = Tokenize(actual);

        if (expTokens.Count == 0 && actTokens.Count == 0)
            return new AccuracyResult(1.0, AccuracyBand.High, 0, 0, 0);

        if (expTokens.Count == 0 || actTokens.Count == 0)
            return new AccuracyResult(0.0, AccuracyBand.Low, 0, expTokens.Count, actTokens.Count);

        var expSet = new HashSet<string>(expTokens);
        var actSet = new HashSet<string>(actTokens);
        var matched = expSet.Intersect(actSet).Count();

        double precision = (double)matched / actSet.Count;
        double recall = (double)matched / expSet.Count;
        double f1 = (precision + recall) == 0 ? 0.0 : 2 * precision * recall / (precision + recall);

        var band = f1 >= 0.8 ? AccuracyBand.High : f1 >= 0.5 ? AccuracyBand.Medium : AccuracyBand.Low;
        return new AccuracyResult(Math.Round(f1, 4), band, matched, expSet.Count, actSet.Count);
    }

    private static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new();
        var normalized = text.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\w\s]", " ");
        return normalized.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
