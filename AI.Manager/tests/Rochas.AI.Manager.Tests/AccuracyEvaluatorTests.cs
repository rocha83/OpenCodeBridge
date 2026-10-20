using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Tests;

public class AccuracyEvaluatorTests
{
    private readonly AccuracyEvaluator _sut = new();

    [Fact]
    public void BothEmpty_ReturnsHigh()
    {
        var r = _sut.Evaluate("", "");
        Assert.Equal(1.0, r.Score);
        Assert.Equal(AccuracyBand.High, r.Band);
    }

    [Fact]
    public void OneEmpty_ReturnsLow()
    {
        var r = _sut.Evaluate("foo bar", "");
        Assert.Equal(0.0, r.Score);
        Assert.Equal(AccuracyBand.Low, r.Band);
    }

    [Fact]
    public void Identical_ReturnsHigh()
    {
        var r = _sut.Evaluate("hello world test", "hello world test");
        Assert.Equal(1.0, r.Score);
        Assert.Equal(AccuracyBand.High, r.Band);
    }

    [Fact]
    public void PartialOverlap_ReturnsMediumOrLow()
    {
        var r = _sut.Evaluate("a b c d", "a b x y");
        Assert.True(r.Score > 0 && r.Score < 1);
    }

    [Fact]
    public void CaseInsensitive()
    {
        var r = _sut.Evaluate("Hello World", "hello world");
        Assert.Equal(1.0, r.Score);
    }
}