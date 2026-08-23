using Xberg.Internal.Pdf;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The XMP packet reader (ISO 32000-1 §14.3.2), ported from pdf_oxide <c>extractors/xmp.rs</c>.
/// Parsing is pure, so these pin its behaviour without needing a PDF.
/// </summary>
public class PdfXmpTests
{
    private const string Packet = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about=""
                xmlns:dc="http://purl.org/dc/elements/1.1/"
                xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                xmlns:pdf="http://ns.adobe.com/pdf/1.3/">
              <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Test Document</rdf:li></rdf:Alt></dc:title>
              <dc:creator><rdf:Seq><rdf:li>Ada Lovelace</rdf:li><rdf:li>Alan Turing</rdf:li></rdf:Seq></dc:creator>
              <dc:description><rdf:Alt><rdf:li xml:lang="x-default">A description.</rdf:li></rdf:Alt></dc:description>
              <dc:subject><rdf:Bag><rdf:li>engines</rdf:li><rdf:li>looms</rdf:li></rdf:Bag></dc:subject>
              <xmp:CreatorTool>UnknownApplication</xmp:CreatorTool>
              <xmp:CreateDate>2008-02-18T13:41:09Z</xmp:CreateDate>
              <xmp:ModifyDate>2009-03-19T14:42:10Z</xmp:ModifyDate>
              <pdf:Producer>GPL Ghostscript</pdf:Producer>
            </rdf:Description>
          </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;

    [Fact]
    public void PropertiesAreReadThroughTheirRdfContainers()
    {
        var xmp = PdfXmp.Parse(Packet);
        Assert.NotNull(xmp);
        // A value's depth varies with its container — a title is wrapped in rdf:Alt/rdf:li, a
        // creator tool is bare — so text is attributed to the nearest non-RDF ancestor.
        Assert.Equal("Test Document", xmp!.DcTitle);
        Assert.Equal("A description.", xmp.DcDescription);
        Assert.Equal("UnknownApplication", xmp.XmpCreatorTool);
        Assert.Equal("2008-02-18T13:41:09Z", xmp.XmpCreateDate);
        Assert.Equal("2009-03-19T14:42:10Z", xmp.XmpModifyDate);
        Assert.Equal("GPL Ghostscript", xmp.PdfProducer);
    }

    [Fact]
    public void BagsAndSequencesKeepEveryEntry()
    {
        var xmp = PdfXmp.Parse(Packet);
        Assert.Equal(new[] { "Ada Lovelace", "Alan Turing" }, xmp!.DcCreator);
        Assert.Equal(new[] { "engines", "looms" }, xmp.DcSubject);
    }

    [Fact]
    public void ALanguageAlternativeKeepsOnlyItsDefault()
    {
        // Every rdf:li under dc:title is the same title in another language; the document has one.
        var xmp = PdfXmp.Parse("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/">
                  <dc:title><rdf:Alt>
                    <rdf:li xml:lang="x-default">Kingfisher</rdf:li>
                    <rdf:li xml:lang="de">Eisvogel</rdf:li>
                  </rdf:Alt></dc:title>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """);
        Assert.Equal("Kingfisher", xmp!.DcTitle);
    }

    [Fact]
    public void EscapedMarkupInAValueKeepsOnlyItsFirstRun()
    {
        // Some producers put escaped HTML in dc:description. Upstream's pull parser reports each
        // entity reference as its own event and the packet reader ignores those, so the value
        // arrives as the runs between them and the first one is what a single-valued property
        // keeps.
        var xmp = PdfXmp.Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:description><rdf:Alt><rdf:li>&lt;div&gt;&lt;p&gt;Abstract&lt;/p&gt;&lt;/div&gt;</rdf:li></rdf:Alt></dc:description>
              </rdf:Description>
            </rdf:RDF>
            """);
        Assert.Equal("div", xmp!.DcDescription);
    }

    [Fact]
    public void TextOutsideAnyXmpPacketIsNotMetadata()
    {
        Assert.Null(PdfXmp.Parse("not xmp at all"));
    }

    [Fact]
    public void APacketNamingNothingReportsAsEmpty()
    {
        var xmp = PdfXmp.Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:xapMM="http://ns.adobe.com/xap/1.0/mm/"
                               xapMM:DocumentID="b5b6260c"/>
            </rdf:RDF>
            """);
        Assert.NotNull(xmp);
        Assert.True(xmp!.IsEmpty);
    }

    /// <summary>
    /// Ghostscript writes character references XML 1.0 forbids into <c>rdf:about</c>. Upstream's
    /// non-validating reader walks straight past them, so the packet's real properties must
    /// survive.
    /// </summary>
    [Fact]
    public void IllegalCharacterReferencesDoNotDiscardTheRestOfThePacket()
    {
        var xmp = PdfXmp.Parse("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="doc&#1;&#8;id" xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Kept title</rdf:li></rdf:Alt></dc:title>
                <dc:description><rdf:Seq><rdf:li>Kept description</rdf:li></rdf:Seq></dc:description>
              </rdf:Description>
            </rdf:RDF>
            </x:xmpmeta>
            """);
        Assert.NotNull(xmp);
        Assert.Equal("Kept title", xmp!.DcTitle);
        Assert.Equal("Kept description", xmp.DcDescription);
    }

    /// <summary>
    /// An entity reference ends the run of character data it sits in, so a single-valued
    /// property keeps only the text before the first one — upstream's reader reports the
    /// reference as its own event and ignores it.
    /// </summary>
    [Fact]
    public void AnEntityReferenceEndsTheRunItInterrupts()
    {
        var xmp = PdfXmp.Parse("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:description><rdf:Alt><rdf:li>&lt;div&gt;Abstract text&lt;/div&gt;</rdf:li></rdf:Alt></dc:description>
                <dc:subject><rdf:Bag><rdf:li>alpha &amp; beta</rdf:li></rdf:Bag></dc:subject>
              </rdf:Description>
            </rdf:RDF>
            </x:xmpmeta>
            """);
        Assert.NotNull(xmp);
        Assert.Equal("div", xmp!.DcDescription);
        Assert.Equal(new[] { "alpha", "beta" }, xmp.DcSubject);
    }

    [Fact]
    public void TextWithoutEntityReferencesStaysOneValue()
    {
        var xmp = PdfXmp.Parse("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title><rdf:Alt><rdf:li>A plain, unescaped title</rdf:li></rdf:Alt></dc:title>
              </rdf:Description>
            </rdf:RDF>
            </x:xmpmeta>
            """);
        Assert.Equal("A plain, unescaped title", xmp!.DcTitle);
    }
}
