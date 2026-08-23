// Ported from pdf_oxide `optional_content.rs` :: compute_default_off_ocgs.
//
// Optional content (ISO 32000-1 §8.11) lets a producer ship layers a viewer hides by default:
// a watermark draft stamp, a second language, CAD annotations. Text inside a hidden layer is
// not part of the page a reader sees, so extraction leaves it out.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Xberg.Internal.Pdf;

internal static class PdfOptionalContent
{
    private static readonly ConditionalWeakTable<PdfDocument, HashSet<string>> Cache = new();

    /// <summary>
    /// The names of the optional-content groups the document's default configuration turns off.
    /// Empty for the common case of a document with no <c>/OCProperties</c>.
    /// </summary>
    public static HashSet<string> DefaultOffOcgs(PdfDocument doc) =>
        Cache.GetValue(doc, Compute);

    private static HashSet<string> Compute(PdfDocument doc)
    {
        var off = new HashSet<string>(System.StringComparer.Ordinal);
        var ocProps = doc.Resolve(doc.Catalog?.Get("OCProperties")).AsDict();
        if (ocProps is null) return off;
        var defaultConfig = doc.Resolve(ocProps.Get("D")).AsDict();
        if (defaultConfig is null) return off;

        string baseState = doc.Resolve(defaultConfig.Get("BaseState")).AsName() ?? "ON";
        if (baseState == "OFF")
        {
            // Everything starts off; only the groups named in /ON come back.
            var on = OcgNames(doc, defaultConfig.Get("ON"));
            foreach (string name in OcgNames(doc, ocProps.Get("OCGs")))
                if (!on.Contains(name)) off.Add(name);
        }
        else
        {
            // /ON or /Unchanged: only the groups named in /OFF are hidden.
            off.UnionWith(OcgNames(doc, defaultConfig.Get("OFF")));
        }
        return off;
    }

    /// <summary>The <c>/Name</c> of every optional-content group an array entry reaches.</summary>
    private static HashSet<string> OcgNames(PdfDocument doc, PdfObject? arrayObj)
    {
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        if (doc.Resolve(arrayObj).AsArray() is not { } array) return names;
        foreach (var item in array.Items)
        {
            var ocg = doc.Resolve(item).AsDict();
            if (ocg?.Get("Name") is not { } nameObj) continue;
            if (nameObj.AsName() is { } name) names.Add(name);
            else if (doc.Resolve(nameObj).AsStringBytes() is { } bytes
                     && PdfMetadataExtractor.DecodePdfString(bytes) is { } decoded)
                names.Add(decoded);
        }
        return names;
    }
}
