using Rochas.AI.Manager.Domain.Helpers;
using Xunit;

namespace Rochas.AI.Manager.Tests;

public class EntityHashCodeTests
{
    [Fact]
    public void Compute_Deterministic()
    {
        uint a = EntityHashCode.Compute("Foo");
        uint b = EntityHashCode.Compute("Foo");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DifferentInputs()
    {
        uint a = EntityHashCode.Compute("Foo");
        uint b = EntityHashCode.Compute("Bar");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_Empty()
    {
        var hash = EntityHashCode.Compute("");
        Assert.True(hash >= 0);
    }
}