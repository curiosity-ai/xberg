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
        string formatString;
        switch (mimeType)
        {
            case "application/x-research-info-systems": citations = CitationParser.ParseRis(text); formatString = "RIS"; break;
            case "application/x-pubmed": citations = CitationParser.ParsePubMed(text); formatString = "PubMed"; break;
            case "application/x-endnote+xml": citations = CitationParser.ParseEndNoteXml(text); formatString = "EndNote XML"; break;
            default:
            {
                var empty = new InternalDocument("citation")
                {
                    MimeType = mimeType,
                    Metadata = new Metadata
                    {
                        Format = new FormatMetadata { FormatType = "citation", Payload = new CitationMetadata { CitationCount = 0, Format = "Unknown" } },
                    },
                };
                return empty;
            }
        }

        var builder = new InternalDocumentBuilder("citation");
        var authorsSet = new SortedSet<string>(StringComparer.Ordinal);
        var keywordsSet = new SortedSet<string>(StringComparer.Ordinal);
        var yearsSet = new SortedSet<uint>();
        var doisVec = new List<string>();

        foreach (var citation in citations)
        {
            foreach (var a in citation.Authors)
            {
                string authorName = a.GivenName is not null ? $"{a.GivenName} {a.Name}" : a.Name;
                if (authorName.Length != 0) authorsSet.Add(authorName);
            }
            if (citation.Year > 0) yearsSet.Add((uint)citation.Year);
            if (!string.IsNullOrEmpty(citation.Doi))
            {
                doisVec.Add(citation.Doi);
                builder.PushUri(MarkupHelpers.Citation($"https://doi.org/{citation.Doi}", citation.Title));
            }
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
        var authorsList = authorsSet.ToList();
        var keywordsList = keywordsSet.ToList();
        var citationMeta = new CitationMetadata
        {
            CitationCount = citations.Count,
            Format = formatString,
            Authors = authorsList,
            YearRange = yearsSet.Count > 0
                ? new YearRange { Min = yearsSet.Min, Max = yearsSet.Max, Years = yearsSet.ToList() }
                : null,
            Dois = doisVec,
            Keywords = keywordsList,
        };
        var meta = new Metadata
        {
            Format = new FormatMetadata { FormatType = "citation", Payload = citationMeta },
        };
        if (authorsList.Count > 0) meta.Authors = authorsList;
        if (keywordsList.Count > 0) meta.Keywords = keywordsList;
        doc.Metadata = meta;
        return doc;
    }
}
