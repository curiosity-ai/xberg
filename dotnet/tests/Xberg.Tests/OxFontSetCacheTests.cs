// Tests for the per-document font-set caches in `PdfDocument::load_fonts` (document.rs:19130):
// the one keyed by the /Font dictionary's object reference and the one keyed by the
// alias-to-reference mapping it spells out. Both cache the extractor's whole font set, which is
// how a page whose /Font dictionary fingerprints like an earlier form's resolves aliases that
// its own resources never mention.
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide;
using Xberg.Internal.PdfOxide.Text;
using Xunit;

namespace Xberg.Tests;

public sealed class OxFontSetCacheTests
{
    private const string Helvetica = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>";
    private const string Symbol = "<</Type/Font/Subtype/Type1/BaseFont/Symbol>>";

    [Fact]
    public void ResourcesLoadOnlyTheFontsTheyName()
    {
        var (doc, objects) = Build(Helvetica, Symbol, "<</Font<</F1 1 0 R>>>>");

        var extractor = NewExtractor(doc);
        extractor.LoadFontsForResources(extractor, objects[2]);

        Assert.Equal(new[] { "F1" }, Aliases(extractor));
    }

    [Fact]
    public void AFingerprintHitCarriesTheWholeFontSetOfTheContextThatStoredIt()
    {
        // A form whose own /Resources name only F1 is loaded on top of a page that also has F3,
        // so the set cached under that fingerprint holds both. A later page whose /Font
        // dictionary names the same alias and reference then resolves F3 as well.
        var (doc, objects) = Build(
            Helvetica, Symbol, "<</Font<</F1 1 0 R/F3 2 0 R>>>>", "<</Font<</F1 1 0 R>>>>");

        var page = NewExtractor(doc);
        page.LoadFontsForResources(page, objects[2]);
        page.LoadFontsForResources(page, objects[3]);

        var later = NewExtractor(doc);
        later.LoadFontsForResources(later, objects[3]);

        Assert.Equal(new[] { "F1", "F3" }, Aliases(later));
    }

    [Fact]
    public void TwoDictionariesNamingTheSameReferencesShareOneCacheEntry()
    {
        // Distinct objects, identical (alias, reference) mapping: the fingerprint is the same.
        var (doc, objects) = Build(
            Helvetica, Symbol, "<</Font<</F1 1 0 R/F3 2 0 R>>>>",
            "<</Font<</F1 1 0 R>>>>", "<</Font<</F1 1 0 R>>>>");

        var page = NewExtractor(doc);
        page.LoadFontsForResources(page, objects[2]);
        page.LoadFontsForResources(page, objects[3]);

        var later = NewExtractor(doc);
        later.LoadFontsForResources(later, objects[4]);

        Assert.Equal(new[] { "F1", "F3" }, Aliases(later));
    }

    [Fact]
    public void ADifferentAliasMappingIsADifferentFingerprint()
    {
        // Same reference under a different alias, so nothing is inherited.
        var (doc, objects) = Build(
            Helvetica, Symbol, "<</Font<</F1 1 0 R/F3 2 0 R>>>>", "<</Font<</F1 1 0 R>>>>",
            "<</Font<</F9 1 0 R>>>>");

        var page = NewExtractor(doc);
        page.LoadFontsForResources(page, objects[2]);
        page.LoadFontsForResources(page, objects[3]);

        var later = NewExtractor(doc);
        later.LoadFontsForResources(later, objects[4]);

        Assert.Equal(new[] { "F9" }, Aliases(later));
    }

    [Fact]
    public void CachesDoNotCrossDocuments()
    {
        var (doc, objects) = Build(
            Helvetica, Symbol, "<</Font<</F1 1 0 R/F3 2 0 R>>>>", "<</Font<</F1 1 0 R>>>>");
        var page = NewExtractor(doc);
        page.LoadFontsForResources(page, objects[2]);
        page.LoadFontsForResources(page, objects[3]);

        var (other, otherObjects) = Build(
            Helvetica, Symbol, "<</Font<</F1 1 0 R/F3 2 0 R>>>>", "<</Font<</F1 1 0 R>>>>");
        var elsewhere = NewExtractor(other);
        elsewhere.LoadFontsForResources(elsewhere, otherObjects[3]);

        Assert.Equal(new[] { "F1" }, Aliases(elsewhere));
    }

    // ---- helpers -----------------------------------------------------------------

    private static OxTextExtractor NewExtractor(PdfDocument doc)
    {
        var extractor = new OxTextExtractor(OxTextExtractionConfig.New());
        extractor.Document = doc;
        return extractor;
    }

    private static string[] Aliases(OxTextExtractor extractor)
    {
        var names = new List<string>();
        foreach ((string name, _) in extractor.GetFontSet())
        {
            names.Add(name);
        }
        names.Sort(System.StringComparer.Ordinal);
        return names.ToArray();
    }

    /// <summary>
    /// Serialize the given object bodies into a document and hand each one back, indexed as
    /// written (object 1 is <c>[0]</c>).
    /// </summary>
    private static (PdfDocument Doc, PdfObject[] Objects) Build(params string[] bodies)
    {
        var outBytes = new List<byte>();
        void Append(string s) => outBytes.AddRange(Encoding.ASCII.GetBytes(s));

        int catalog = bodies.Length + 1;
        Append("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(outBytes.Count);
            Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        offsets.Add(outBytes.Count);
        Append($"{catalog} 0 obj\n<</Type/Catalog>>\nendobj\n");

        int xrefPos = outBytes.Count;
        Append("xref\n");
        Append($"0 {catalog + 1}\n");
        Append("0000000000 65535 f \n");
        foreach (int off in offsets) Append(off.ToString("D10") + " 00000 n \n");
        Append($"trailer\n<</Size {catalog + 1}/Root {catalog} 0 R>>\n");
        Append($"startxref\n{xrefPos}\n%%EOF");

        var doc = PdfDocument.Open(outBytes.ToArray());
        var objects = new PdfObject[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            objects[i] = doc.LoadObject(i + 1, 0)!;
        }
        return (doc, objects);
    }
}
