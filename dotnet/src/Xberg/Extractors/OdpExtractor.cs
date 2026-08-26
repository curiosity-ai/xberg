// Ported from crates/xberg/src/extractors/odp.rs (OdpExtractor + extract_content).

using System.IO.Compression;
using Xberg.Core;
using Xberg.Internal.Odf;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Native ODP (OpenDocument Presentation) extractor. Mirrors Rust <c>OdpExtractor</c>.
/// </summary>
public sealed class OdpExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes =>
        new[] { "application/vnd.oasis.opendocument.presentation" };

    public int Priority => 60;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        byte[] bytes = content.ToArray();

        InternalDocument doc;
        using (var stream = new MemoryStream(bytes, writable: false))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            doc = OdfPresentationParser.BuildInternalDocument(archive, config.SecurityLimits);
        }
        doc.MimeType = mimeType;

        // A presentation's meta.xml uses the same ODF schema as a text document's.
        OdtProperties props;
        using (var metaStream = new MemoryStream(bytes, writable: false))
        using (var metaArchive = new ZipArchive(metaStream, ZipArchiveMode.Read))
        {
            props = OdfMetadata.Extract(metaArchive);
        }

        doc.Metadata = OdtExtractor.BuildMetadata(props);
        return doc;
    }
}
