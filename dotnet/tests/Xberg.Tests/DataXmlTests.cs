using System.Text;
using Xberg.Core;
using Xberg.Extractors;
using Xberg.Types;
using Xunit;

namespace Xberg.Tests;

/// <summary>Tests for <see cref="XmlExtractor"/>. Ports the Rust `extraction/xml.rs` tests.</summary>
public class DataXmlTests
{
    private static InternalDocument Extract(string xml, string mime = "application/xml") =>
        new XmlExtractor().Extract(Encoding.UTF8.GetBytes(xml), mime, new ExtractionConfig());

    private static XmlMetadata Meta(InternalDocument doc) =>
        Assert.IsType<XmlMetadata>(doc.Metadata.Format!.Payload);

    [Fact]
    public void SimpleXml_CountsElementsAndUniqueNames()
    {
        var doc = Extract("<root><item>Hello</item><item>World</item></root>");
        var m = Meta(doc);
        Assert.Equal(3u, m.ElementCount);
        Assert.Equal(new[] { "item", "root" }, m.UniqueElements);
    }

    [Fact]
    public void UniqueElements_AreSorted()
    {
        var m = Meta(Extract("<root><z/><a/><m/><b/></root>"));
        Assert.Equal(new[] { "a", "b", "m", "root", "z" }, m.UniqueElements);
    }

    [Fact]
    public void SelfClosingTags_Counted()
    {
        var m = Meta(Extract("<root><item1/><item2/><item3/></root>"));
        Assert.Equal(4u, m.ElementCount);
        Assert.Equal(4, m.UniqueElements.Count);
    }

    [Fact]
    public void Elements_BecomeHeadings_TextBecomesParagraphs()
    {
        var doc = Extract("<note><to>Tove</to></note>");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "note" && e.Kind.Level == 1);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Heading && e.Text == "to" && e.Kind.Level == 2);
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text == "Tove");
    }

    [Fact]
    public void Attributes_RenderedInPlain()
    {
        string plain = Xberg.Rendering.PlainRenderer.Render(Extract("<root id=\"1\"><item type=\"test\">C</item></root>"));
        Assert.Contains("item (type: test)", plain);
    }

    [Fact]
    public void Cdata_ExtractedAsText()
    {
        var doc = Extract("<root><![CDATA[Special <chars> & data]]></root>");
        Assert.Contains(doc.Elements, e => e.Kind.Tag == ElementKindTag.Paragraph && e.Text == "Special <chars> & data");
        Assert.Equal(1u, Meta(doc).ElementCount);
    }

    [Fact]
    public void AnEntityReferenceIsPartOfTheTextAroundIt()
    {
        // A reference is a spelling of a character, not a boundary: this is one country's name.
        var doc = Extract("<root>Trinidad &amp; Tobago</root>");
        var paras = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();
        Assert.Equal(new[] { "Trinidad & Tobago" }, paras);
    }

    [Fact]
    public void CharacterReferencesResolveInBothDecimalAndHex()
    {
        var doc = Extract("<root>caf&#233; and caf&#xE9;</root>");
        var paras = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();
        Assert.Equal(new[] { "café and café" }, paras);
    }

    [Fact]
    public void AnUnresolvableReferenceContributesNothing()
    {
        // A reference this parser cannot resolve names something the document did not carry
        // inline, and its literal spelling is not that thing.
        var doc = Extract("<root>before &unknownentity; after</root>");
        var paras = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();
        Assert.Equal(new[] { "before  after" }, paras);
    }

    [Fact]
    public void Svg_OnlyTextBearingElementsContributeText()
    {
        string svg = "<svg><style>.c{fill:red}</style><text>Visible</text></svg>";
        var doc = Extract(svg, "image/svg+xml");
        var paras = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();
        Assert.Contains("Visible", paras);
        Assert.DoesNotContain(paras, p => p.Contains("fill"));
    }

    [Fact]
    public void SupportedMimeTypes_MatchRust()
    {
        var mimes = new XmlExtractor().SupportedMimeTypes.ToList();
        Assert.Equal(new[] { "application/xml", "text/xml", "image/svg+xml", "application/x-endnote+xml" }, mimes);
    }

    /// <summary>
    /// The SVG text-element filter guards only text nodes. A CDATA section — which is how an SVG
    /// carries its script and style bodies — is kept wherever it appears; on one flamegraph
    /// fixture that is half the document.
    /// </summary>
    [Fact]
    public void SvgKeepsCdataOutsideTextElements()
    {
        var doc = Extract("<svg><script type=\"text/ecmascript\"><![CDATA[var a = 1;]]></script>" +
                          "<rect/><text>Visible</text></svg>", "image/svg+xml");
        var paras = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Paragraph).Select(e => e.Text).ToList();
        Assert.Contains("var a = 1;", paras);
        Assert.Contains("Visible", paras);
    }

    /// <summary>
    /// Upstream used to compute an element's heading level as `((depth as u8) + 1).min(6)` over a
    /// u16 depth: the cast kept only the low byte and the add wrapped, so a document that never
    /// closes its tags walked past depth 255 and its levels started over from 0 instead of staying
    /// pinned at 6. The port reproduced that deliberately. Upstream `fix(xml): clamp heading depth
    /// before narrowing` clamps in the wide type first, so the level now stays in 1..=6 at every
    /// depth — including the docling `.doctags.txt` groundtruth files that reach past 255.
    /// </summary>
    [Fact]
    public void PastDepth255TheHeadingLevelStaysPinnedAtSix()
    {
        // 258 tags, none closed: the element opened at depth 255 is the one that used to wrap.
        var doc = Extract("<r>" + string.Concat(Enumerable.Range(0, 257).Select(i => $"<t{i}>")));
        var levels = doc.Elements.Where(e => e.Kind.Tag == ElementKindTag.Heading)
                        .Select(e => e.Kind.Level).ToList();

        Assert.Equal(258, levels.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 6 }, levels.Take(7).ToArray());
        // Depth 255 and everything past it stay at the ceiling instead of wrapping to 0.
        Assert.Equal(new byte[] { 6, 6, 6, 6 }, levels.Skip(254).Take(4).ToArray());
        Assert.All(levels, level => Assert.InRange(level, (byte)1, (byte)6));
    }
}
