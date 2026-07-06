using System.Text;
using Xberg.Core;
using Xberg.Internal.Citation;
using Xberg.Internal.Markup;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Citation-format extractor (RIS, PubMed/MEDLINE, EndNote XML). Ported from Rust
/// `extractors/citation.rs` (which uses the `biblib` crate). Each parsed citation becomes a
/// Citation element (title text). The C# <see cref="CitationMetadata"/> payload is a stub
/// (citation_count only); format/year_range/dois are not represented (documented gap).
/// </summary>
public sealed class CitationExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/x-research-info-systems", "application/x-pubmed", "application/x-endnote+xml",
    };
    public int Priority => 60;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string text = Encoding.UTF8.GetString(content);

        // The Rust extractor also records the format name ("RIS"/"PubMed"/"EndNote XML") in
        // CitationMetadata.format, but the C# CitationMetadata payload is a stub without that
        // field (documented gap), so the dispatch only selects the parser here.
        List<Citation> citations;
        switch (mimeType)
        {
            case "application/x-research-info-systems": citations = CitationParser.ParseRis(text); break;
            case "application/x-pubmed": citations = CitationParser.ParsePubMed(text); break;
            case "application/x-endnote+xml": citations = CitationParser.ParseEndNoteXml(text); break;
            default:
            {
                var empty = new InternalDocument("citation")
                {
                    MimeType = mimeType,
                    Metadata = new Metadata
                    {
                        Format = new FormatMetadata { FormatType = "citation", Payload = new CitationMetadata { CitationCount = 0 } },
                    },
                };
                return empty;
            }
        }

        var builder = new InternalDocumentBuilder("citation");
        var authorsSet = new SortedSet<string>(StringComparer.Ordinal);
        var keywordsSet = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var citation in citations)
        {
            foreach (var a in citation.Authors)
            {
                string authorName = a.GivenName is not null ? $"{a.GivenName} {a.Name}" : a.Name;
                if (authorName.Length != 0) authorsSet.Add(authorName);
            }
            if (citation.Year > 0) { /* year_range tracked in stub-only metadata */ }
            if (!string.IsNullOrEmpty(citation.Doi))
                builder.PushUri(MarkupHelpers.Citation($"https://doi.org/{citation.Doi}", citation.Title));
            foreach (var kw in citation.Keywords) if (kw.Length != 0) keywordsSet.Add(kw);
        }

        for (int i = 0; i < citations.Count; i++)
        {
            string title = citations[i].Title;
            string key = title.Length == 0 ? $"citation_{i + 1}" : title;
            builder.PushCitation(title, key, null);
        }

        var doc = builder.Build();
        doc.MimeType = mimeType;
        var meta = new Metadata
        {
            Format = new FormatMetadata { FormatType = "citation", Payload = new CitationMetadata { CitationCount = citations.Count } },
        };
        if (authorsSet.Count > 0) meta.Authors = authorsSet.ToList();
        if (keywordsSet.Count > 0) meta.Keywords = keywordsSet.ToList();
        doc.Metadata = meta;
        return doc;
    }
}
