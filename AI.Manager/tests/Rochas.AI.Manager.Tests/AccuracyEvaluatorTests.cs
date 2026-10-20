using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Tests;

public class AccuracyEvaluatorTests
{
    private readonly AccuracyEvaluator _sut = new();

    [Fact]
    public void BothEmpty_ReturnsHigh()
    {
        var result = _sut.Evaluate("", "");
        Assert.Equal(1.0, result.Score);
        Assert.Equal(AccuracyBand.High, result.Band);
    }

    [Fact]
    public void OneEmpty_ReturnsLow()
    {
        var result = _sut.Evaluate("foo bar", "");
        Assert.Equal(0.0, result.Score);
        Assert.Equal(AccuracyBand.Low, result.Band);
    }

    [Fact]
    public void Identical_ReturnsHigh()
    {
        var result = _sut.Evaluate("hello world test", "hello world test");
        Assert.Equal(1.0, result.Score);
        Assert.Equal(AccuracyBand.High, result.Band);
    }

    [Fact]
    public void PartialOverlap_ReturnsMediumOrLow()
    {
        var result = _sut.Evaluate("a b c d", "a b x y");
        Assert.True(result.Score > 0 && result.Score < 1);
    }

    [Fact]
    public void CaseInsensitive()
    {
        var result = _sut.Evaluate("Hello World", "hello world");
        Assert.Equal(1.0, result.Score);
    }
}