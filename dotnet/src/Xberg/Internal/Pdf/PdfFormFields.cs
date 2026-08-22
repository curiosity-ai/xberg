// AcroForm field extraction, ported from pdf_oxide's `FormExtractor::extract_fields`
// (extractors/forms.rs) plus the value mapping in crates/xberg/src/pdf/oxide/forms.rs
// (`map_form_field` / `map_field_value`).
//
// Distinct from the per-page widget scan in `PdfExtractor.CollectWidgetTextValues`: that one
// splices values into a page's text at extraction time, this one models the document-wide
// field tree with fully qualified names so filled values can be surfaced as document elements.
using System.Text;
using Xberg.Internal.PdfOxide.Fonts;

namespace Xberg.Internal.Pdf;

/// <summary>One AcroForm field: partial name, fully qualified name, and rendered value.</summary>
internal sealed class PdfAcroFormField
{
    public string Name = "";
    public string FullName = "";
    public string? Value;
}

internal static class PdfFormFields
{
    /// <summary>A /Parent or /Kids cycle would otherwise recurse forever on a malformed file.</summary>
    private const int MaxFieldDepth = 32;

    /// <summary>Walk <c>/Root /AcroForm /Fields</c> depth-first, in document order.</summary>
    public static List<PdfAcroFormField> Extract(PdfDocument pdf)
    {
        var result = new List<PdfAcroFormField>();
        var acroForm = pdf.Resolve(pdf.Catalog?.Get("AcroForm")).AsDict();
        var fields = pdf.Resolve(acroForm?.Get("Fields")).AsArray();
        if (fields is null) return result;
        foreach (var field in fields.Items) ExtractRecursive(pdf, field, "", result, 0);
        return result;
    }

    private static void ExtractRecursive(
        PdfDocument pdf, PdfObject? fieldRef, string parentName, List<PdfAcroFormField> result, int depth)
    {
        if (depth > MaxFieldDepth) return;
        var dict = pdf.Resolve(fieldRef).AsDict();
        if (dict is null) return;

        string partialName = pdf.Resolve(dict.Get("T")).AsStringBytes() is { } nameBytes
            ? DecodeTextString(nameBytes)
            : "";

        string fullName = parentName.Length == 0 ? partialName
            : partialName.Length == 0 ? parentName
            : parentName + "." + partialName;

        // Kids are visited before the parent is judged, so a grouping node that is itself
        // skipped still contributes its name to the children's qualified names.
        if (pdf.Resolve(dict.Get("Kids")).AsArray() is { } kids)
            foreach (var kid in kids.Items) ExtractRecursive(pdf, kid, fullName, result, depth + 1);

        string? fieldType = pdf.Resolve(dict.Get("FT")).AsName();
        // A parent carrying /T but no /FT (a logical grouping such as a multi-box SSN field)
        // is still surfaced; only a node with neither is nothing to report.
        if (fieldType is null && partialName.Length == 0) return;

        result.Add(new PdfAcroFormField
        {
            Name = partialName,
            FullName = fullName,
            Value = MapFieldValue(pdf.Resolve(dict.Get("V")), fieldType),
        });
    }

    /// <summary>
    /// The field's /V rendered as the single string the typed model carries: text verbatim,
    /// booleans as <c>true</c>/<c>false</c>, names as themselves, multi-select arrays joined.
    /// </summary>
    private static string? MapFieldValue(PdfObject? value, string? fieldType) => value switch
    {
        PdfString s => DecodeTextString(s.Bytes),
        // Checkbox and radio states are the only names read as booleans, and only the four
        // conventional on/off spellings; any other name is a radio group's export value.
        PdfName n when fieldType == "Btn" => n.Value switch
        {
            "Yes" or "On" => "true",
            "No" or "Off" => "false",
            _ => n.Value,
        },
        PdfName n => n.Value,
        PdfBool b => b.Value ? "true" : "false",
        PdfArray a => JoinArrayValue(a),
        _ => null,
    };

    private static string? JoinArrayValue(PdfArray array)
    {
        var parts = new List<string>();
        foreach (var item in array.Items)
        {
            if (item is PdfString s) parts.Add(DecodeTextString(s.Bytes));
            else if (item is PdfName n) parts.Add(n.Value);
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// UTF-16BE when the BOM says so, PDFDocEncoding otherwise (ISO 32000-1 §7.9.2.2).
    /// Bytes with no PDFDocEncoding character are dropped rather than replaced.
    /// </summary>
    private static string DecodeTextString(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            var utf16 = new StringBuilder();
            for (int i = 2; i + 1 < bytes.Length; i += 2) utf16.Append((char)((bytes[i] << 8) | bytes[i + 1]));
            // Ill-formed UTF-16 has no decoding at all upstream, and half a surrogate pair
            // rendered as a replacement character would be worse than reporting no value.
            return IsWellFormedUtf16(utf16) ? utf16.ToString() : "";
        }
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            if (OxEncodingTables.PdfDocEncodingLookup(b) is { } c) sb.Append(c);
        return sb.ToString();
    }

    private static bool IsWellFormedUtf16(StringBuilder text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                return false;
            i++;
        }
        return true;
    }
}
