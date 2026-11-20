using Numbering.Core;

namespace Numbering.Core.Tests;

public sealed class NumberTemplateTests
{
    [Theory]
    [InlineData("Д-{Уровень}-{N:000}", 2, "Д-01-002")]
    [InlineData("{Префикс}{N:00}", 2, "Д02")]
    [InlineData("{N:000}", 1001, "1001")]
    [InlineData("% {N:0} {Уровень}", 9, "% 9 01")]
    public void RendersLiteralTextAndTokens(string pattern, int number, string expected)
    {
        Assert.True(NumberTemplate.TryParse(pattern, out var template, out var error), error);
        Assert.Equal(expected, template!.Render("Д", "01", number));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{Уровень}")]
    [InlineData("{N:0}{N:00}")]
    [InlineData("{N:00")]
    [InlineData("{N:00}}")]
    [InlineData("{{N:00}")]
    [InlineData("{N:}")]
    [InlineData("{N:0x}")]
    [InlineData("{N:0000000000}")]
    [InlineData("{Этаж}-{N:000}")]
    public void GivesAnErrorForInvalidPatterns(string pattern)
    {
        Assert.False(NumberTemplate.TryParse(pattern, out var template, out var error));
        Assert.Null(template);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
