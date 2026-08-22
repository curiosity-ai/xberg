using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// How Typst's citation forms reach the extracted document.
/// </summary>
public class TypstCitationTests
{
    private static InternalDocument Parse(string source) =>
        new TypstExtractor().Extract(
            Encoding.UTF8.GetBytes(source), "application/x-typst", new ExtractionConfig());

    private static string Prose(InternalDocument doc) =>
        string.Join("\n", doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text));

    private static List<string> Citations(InternalDocument doc) =>
        doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Citation).Select(e => e.Text).ToList();

    [Fact]
    public void AReferenceKeepsItsMarkerInTheProseAndBecomesACitation()
    {
        // Leaving the raw form puts Typst source into the prose; dropping it loses the reference.
        var doc = Parse("As shown in @eq:equation, the result holds.\n");
        Assert.Contains("[eq:equation]", Prose(doc));
        Assert.Equal(new[] { "eq:equation" }, Citations(doc));
    }

    [Fact]
    public void TheExplicitCiteFormIsRecognisedToo()
    {
        var doc = Parse("See #cite(<knuth1984>) for the original.\n");
        Assert.Contains("[knuth1984]", Prose(doc));
        Assert.Equal(new[] { "knuth1984" }, Citations(doc));
    }

    [Fact]
    public void AnAtSignInsideAWordIsNotAReference()
    {
        // An email address would otherwise lose its domain to a citation.
        var doc = Parse("Write to ada@example.com for details.\n");
        Assert.Contains("ada@example.com", Prose(doc));
        Assert.Empty(Citations(doc));
    }

    [Fact]
    public void AReferenceAfterAnOpeningParenthesisIsStillAReference()
    {
        var doc = Parse("The result (@thm:main) follows.\n");
        Assert.Contains("([thm:main])", Prose(doc));
        Assert.Equal(new[] { "thm:main" }, Citations(doc));
    }
}
