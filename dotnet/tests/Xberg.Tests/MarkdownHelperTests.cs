using Xberg.Rendering;
using Xunit;

namespace Xberg.Tests;

/// <summary>Ported from the pure string-helper tests in Rust `rendering/markdown.rs`.</summary>
public class MarkdownHelperTests
{
    [Fact]
    public void UnescapeNoTargetsUnchanged()
    {
        const string input = "hello world no escapes here";
        Assert.Equal(input, MarkdownRenderer.UnescapeBackslashSequences(input, new[] { '_', '[', ']', '(', ')' }));
    }

    [Fact]
    public void UnescapeSingleHit()
    {
        Assert.Equal("hello_world", MarkdownRenderer.UnescapeBackslashSequences("hello\\_world", new[] { '_' }));
    }

    [Fact]
    public void UnescapeMultipleTargets()
    {
        Assert.Equal("[link](url) and [another]",
            MarkdownRenderer.UnescapeBackslashSequences("\\[link\\](url\\) and \\[another\\]", new[] { '[', ']', '(', ')' }));
    }

    [Fact]
    public void UnescapeBackslashNotFollowedByTargetKept()
    {
        Assert.Equal("foo\\nbar", MarkdownRenderer.UnescapeBackslashSequences("foo\\nbar", new[] { '_' }));
    }

    [Fact]
    public void ReplaceHtmlEntitiesNewlineBecomesSpace()
    {
        Assert.Equal("line1 line2", MarkdownRenderer.ReplaceHtmlEntities("line1&#10;line2"));
    }

    [Fact]
    public void ReplaceHtmlEntitiesStxRemoved()
    {
        Assert.Equal("beforeafter", MarkdownRenderer.ReplaceHtmlEntities("before&#2;after"));
    }

    [Fact]
    public void ReplaceHtmlEntitiesUnknownKept()
    {
        Assert.Equal("a&#42;b", MarkdownRenderer.ReplaceHtmlEntities("a&#42;b"));
    }

    [Fact]
    public void CollapseTripleNewline()
    {
        Assert.Equal("a\n\nb", MarkdownRenderer.CollapseExcessNewlines("a\n\n\nb"));
    }

    [Fact]
    public void CollapseManyNewlines()
    {
        Assert.Equal("a\n\nb", MarkdownRenderer.CollapseExcessNewlines("a\n\n\n\n\n\nb"));
    }

    [Fact]
    public void CollapseNoTripleUnchanged()
    {
        const string input = "line1\n\nline2\n";
        Assert.Equal(input, MarkdownRenderer.CollapseExcessNewlines(input));
    }
}
