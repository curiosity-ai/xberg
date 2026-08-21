using Xberg.Core;
using Xberg.Extractors;
using Xunit;

namespace Xberg.Tests;

/// <summary>Covers how the DocBook reader treats XML references and definition lists.</summary>
public class DocbookEntityTests
{
    private static string Plain(string xml) =>
        new DocbookExtractor().Extract(
            System.Text.Encoding.UTF8.GetBytes(xml), "application/docbook+xml", new ExtractionConfig())
            .Elements[^1].Text;

    private static string Doc(string body) =>
        $"<article xmlns=\"http://docbook.org/ns/docbook\"><para>{body}</para></article>";

    /// <summary>
    /// A reference is part of the run of text around it, not a break in it. Treating one as a
    /// boundary and discarding it turns `print &amp;quot;working&amp;quot;;` into `print working ;`.
    /// </summary>
    [Fact]
    public void PredefinedReferencesResolveIntoTheSurroundingRun()
    {
        Assert.Equal("print \"working\";", Plain(Doc("print &quot;working&quot;;")));
        Assert.Equal("2 > 1", Plain(Doc("2 &gt; 1")));
        Assert.Equal("a & b", Plain(Doc("a &amp; b")));
    }

    /// <summary>Character references decode to their code point, in either base.</summary>
    [Fact]
    public void CharacterReferencesDecode()
    {
        Assert.Equal("A→B", Plain(Doc("A&#8594;B")));
        Assert.Equal("A→B", Plain(Doc("A&#x2192;B")));
    }

    /// <summary>
    /// An entity the reader has no declaration for resolves to nothing rather than to its own
    /// source text — a document defining `&amp;GHC;` in its DTD must not leak the reference.
    /// </summary>
    [Fact]
    public void AnUndeclaredEntityResolvesToNothing()
    {
        Assert.Equal("compiled it", Plain(Doc("&GHC; compiled it")));
    }

    /// <summary>A non-breaking space is named but not one of the five predefined entities.</summary>
    [Fact]
    public void NbspResolvesToItsCharacter()
    {
        Assert.Equal("a b", Plain(Doc("a&nbsp;b")));
    }
}
