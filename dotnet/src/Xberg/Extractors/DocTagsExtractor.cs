using System.Text;
using Xberg.Core;
using Xberg.Internal.DocTags;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Docling DocTags extractor, ported from Rust <c>extractors/doctags.rs</c> — a thin adapter over
/// <c>DocTagsParser</c>, which holds the parsing itself.
/// </summary>
public sealed class DocTagsExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes =>
        new[] { DocTagsMime.MimeType, DocTagsMime.ApplicationMimeType };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        SecurityBudget.FromConfig(config).AccountText(content.Length);
        var doc = DocTagsParser.Parse(Encoding.UTF8.GetString(content));
        doc.MimeType = mimeType;
        return doc;
    }
}
