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
}
