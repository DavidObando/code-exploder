using Xunit;

namespace CodeExploder.Llm.Tests;

public sealed class TokenEstimatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcdefgh", 2)]
    [InlineData("abcdefghijkl", 3)]
    public void EstimatesCharsOverFourWithMinimumOneForNonEmpty(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }
}
