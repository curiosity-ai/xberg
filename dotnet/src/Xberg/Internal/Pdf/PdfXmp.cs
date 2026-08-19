// XMP metadata extraction — ports pdf_oxide `extractors/xmp.rs` (the packet reader) and the
// field-precedence rules in crates/xberg/src/pdf/oxide/metadata.rs.

using System.Text;
using System.Xml;

namespace Xberg.Internal.Pdf;

/// <summary>
/// The fields an XMP packet contributes to a PDF's metadata (ISO 32000-1 §14.3.2).
/// </summary>
/// <remarks>
/// XMP is the richer of a PDF's two metadata channels and the only one many modern producers
/// write, so a document whose Info dictionary is empty can still carry a full title, author list
/// and dates here. Only the fields the extractor actually surfaces are kept; the rest of the
/// packet is read past.
/// </remarks>
internal sealed class XmpMetadata
{
    public string? DcTitle;
    public List<string> DcCreator = new();
    public string? DcDescription;
    public List<string> DcSubject = new();
    public string? XmpCreatorTool;
    public string? XmpCreateDate;
    public string? XmpModifyDate;
    public string? PdfProducer;
    public string? PdfKeywords;

    /// <summary>Whether the packet parsed but carried nothing worth reporting.</summary>
    public bool IsEmpty =>
        DcTitle is null && DcCreator.Count == 0 && DcDescription is null && DcSubject.Count == 0
        && XmpCreatorTool is null && XmpCreateDate is null && XmpModifyDate is null
        && PdfProducer is null && PdfKeywords is null;
}

internal static class PdfXmp
{
    /// <summary>
    /// Read the catalog's <c>/Metadata</c> stream, if there is one, and parse the XMP packet in
    /// it. Returns null when the document has no XMP, when the packet will not parse, or when it
    /// parsed but named nothing — all ordinary outcomes, since XMP is optional.
    /// </summary>
    public static XmpMetadata? Extract(PdfDocument doc)
    {
        try
        {
            var catalog = doc.Catalog;
            if (catalog is null) return null;
            if (doc.Resolve(catalog.Get("Metadata")) is not PdfStream stream) return null;

            byte[] bytes = doc.DecodeStream(stream);
            if (bytes.Length == 0) return null;

            var xmp = Parse(DecodeXmlBytes(bytes));
            return xmp is null || xmp.IsEmpty ? null : xmp;
        }
        catch (Exception)
        {
            // A malformed packet costs the fields it would have carried, nothing else.
            return null;
        }
    }

    /// <summary>Decode the packet's bytes, honouring a UTF-16 byte-order mark if present.</summary>
    private static string DecodeXmlBytes(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Parse one XMP packet.
    /// </summary>
    /// <remarks>
    /// The packet is sliced out of its surroundings first: a <c>/Metadata</c> stream is an XMP
    /// <em>packet</em>, wrapped in <c>&lt;?xpacket?&gt;</c> processing instructions and padded
    /// with whitespace, which is not a well-formed XML document.
    /// <para>
    /// Property values live at varying depths — a title is
    /// <c>dc:title/rdf:Alt/rdf:li</c>, a plain property is its own text — so rather than match
    /// paths, each run of text is attributed to the nearest enclosing element that is not RDF
    /// scaffolding. That is what upstream does, and it reads every shape a producer might use.
    /// </para>
    /// </remarks>
    public static XmpMetadata? Parse(string xml)
    {
        int start = xml.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        if (start < 0) start = xml.IndexOf("<rdf:RDF", StringComparison.Ordinal);
        int end = xml.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal);
        string closing = "</x:xmpmeta>";
        if (end < 0)
        {
            end = xml.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal);
            closing = "</rdf:RDF>";
        }
        if (start < 0 || end < 0 || end < start) return null;

        string content = xml.Substring(start, end + closing.Length - start);
        var metadata = new XmpMetadata();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            NameTable = new NameTable(),
        };

        var stack = new List<string>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (!reader.IsEmptyElement) stack.Add(reader.Name);
                        break;
                    case XmlNodeType.EndElement:
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    {
                        string text = reader.Value.Trim();
                        if (text.Length == 0) break;
                        string? property = null;
                        for (int i = stack.Count - 1; i >= 0; i--)
                        {
                            string el = stack[i];
                            if (el.StartsWith("rdf:", StringComparison.Ordinal)
                                || el.StartsWith("x:", StringComparison.Ordinal)) continue;
                            property = el;
                            break;
                        }
                        if (property is not null) Assign(metadata, property, text);
                        break;
                    }
                }
            }
        }
        catch (XmlException)
        {
            // Whatever was read before the packet went malformed still counts.
        }

        return metadata;
    }

    private static void Assign(XmpMetadata m, string property, string text)
    {
        switch (property)
        {
            // A title, description or copyright is one value even when the producer wrote a
            // language alternative for each locale: the first is the default and wins.
            case "dc:title": m.DcTitle ??= text; break;
            case "dc:description": m.DcDescription ??= text; break;
            // Creators and subjects are bags — every entry belongs.
            case "dc:creator": m.DcCreator.Add(text); break;
            case "dc:subject": m.DcSubject.Add(text); break;
            case "xmp:CreatorTool": m.XmpCreatorTool = text; break;
            case "xmp:CreateDate": m.XmpCreateDate = text; break;
            case "xmp:ModifyDate": m.XmpModifyDate = text; break;
            case "pdf:Producer": m.PdfProducer = text; break;
            case "pdf:Keywords": m.PdfKeywords = text; break;
        }
    }
}
