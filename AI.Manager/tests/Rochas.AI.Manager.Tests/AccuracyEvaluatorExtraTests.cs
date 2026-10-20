using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Infrastructure.Data;
using Xunit;

namespace Rochas.AI.Manager.Tests;

public class AccuracyEvaluatorExtraTests
{
    private readonly AccuracyEvaluator _sut = new();

    [Theory]
    [InlineData("a b c", "a b c", 1.0)]
    [InlineData("a b c d", "a b", 0.0)]
    public void Score_Computed(string exp, string act, double expectedMin)
    {
        var r = _sut.Evaluate(exp, act);
        if (expectedMin == 1.0) Assert.Equal(1.0, r.Score);
        else Assert.True(r.Score >= 0);
    }

    [Fact]
    public void Band_High()
    {
        var r = _sut.Evaluate("x y z", "x y z w");
        Assert.Equal(AccuracyBand.High, r.Band);
    }

    [Fact]
    public void Band_Low()
    {
        var r = _sut.Evaluate("a b c d e", "x");
        Assert.Equal(AccuracyBand.Low, r.Band);
    }

    [Fact]
    public void Matched_Count()
    {
        var r = _sut.Evaluate("a b c", "a b d");
        Assert.Equal(2, r.MatchedTokens);
    }
}