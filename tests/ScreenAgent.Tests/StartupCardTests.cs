using ScreenAgent.Core;
using Xunit;

namespace ScreenAgent.Tests;

public class StartupCardTests
{
    [Theory]
    [InlineData("a#abcdef · Karte 12345", "a#abcdef")]
    [InlineData("Karte 12345", "card:12345")]
    [InlineData("a#abcdef", "a#abcdef")]
    [InlineData("Karte 12345\na#ABCDEF · Karte 12345", "a#abcdef")]
    [InlineData("ordinary text", null)]
    [InlineData("a#abcdefg", null)]
    public void FindsDistinctReference(string text, string? expected)
    {
        Assert.Equal(expected, StartupCard.Find(text, out var ambiguous));
        Assert.False(ambiguous);
    }

    [Fact]
    public void UnrelatedCardsAreAmbiguous()
    {
        Assert.Null(StartupCard.Find("a#abcdef · Karte 12345\nKarte 67890", out var ambiguous));
        Assert.True(ambiguous);
    }
}
