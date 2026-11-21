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

    [Theory]
    [InlineData("20081", "card:20081")]
    [InlineData(" 20081 ", "card:20081")]
    [InlineData("#20081", "card:20081")]
    [InlineData("c029b6", "a#c029b6")]
    [InlineData("a#C029B6", "a#c029b6")]
    [InlineData("Karte-Nr 20081", "card:20081")]
    [InlineData("Karte Nr. 20081", "card:20081")]
    [InlineData("card 20081", "card:20081")]
    [InlineData("123456", "a#123456")]
    [InlineData("870805", "a#870805")]
    [InlineData(" 870805 ", "a#870805")]
    [InlineData("1234567", "card:1234567")]
    [InlineData("12", null)]
    [InlineData("hello", null)]
    public void FindsBareSelection(string selected, string? expected)
    {
        Assert.Equal(expected, StartupCard.FindSelected(selected, out var ambiguous));
        Assert.False(ambiguous);
    }

    [Fact]
    public void BareNumbersCountOnlyInSelections()
    {
        Assert.Null(StartupCard.Find("20081", out _));
        Assert.Null(StartupCard.Find("c029b6", out _));
    }

    [Fact]
    public void UnrelatedCardsAreAmbiguous()
    {
        Assert.Null(StartupCard.Find("a#abcdef · Karte 12345\nKarte 67890", out var ambiguous));
        Assert.True(ambiguous);
    }
}
