// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`GzipExtractor`).

using Xberg.Core;
using Xberg.Internal.Archive;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Decompresses gzip files (and .tar.gz) and extracts text content.</summary>
public sealed class GzipExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/gzip",
        "application/x-gzip",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var read = GzipReader.Read(content.ToArray());
        return ArchiveDocument.Build(read, mimeType, config);
    }
}
