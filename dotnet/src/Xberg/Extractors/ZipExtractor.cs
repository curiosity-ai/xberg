// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`ZipExtractor`).

using Xberg.Core;
using Xberg.Internal.Archive;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Extracts file lists and text content from ZIP archives, and recursively
/// extracts each child through the public pipeline into <c>Children</c>.</summary>
public sealed class ZipExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/zip",
        "application/x-zip-compressed",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var read = ZipReader.Read(content.ToArray());
        return ArchiveDocument.Build(read, mimeType, config);
    }
}
