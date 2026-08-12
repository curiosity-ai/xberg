// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`SevenZExtractor`) +
// `extraction/archive/sevenz.rs` (the `sevenz-rust2` crate is replaced by the managed
// Internal/SevenZip reader: 7z container parsing + LZMA/LZMA2 decoding).

using Xberg.Core;
using Xberg.Internal.Archive;
using Xberg.Internal.SevenZip;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>Extracts file lists and text content from 7z archives, and recursively
/// extracts each child through the public pipeline into <c>Children</c>.</summary>
public sealed class SevenZipExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-7z-compressed" };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var read = SevenZipReader.Read(content.ToArray());
        return ArchiveDocument.Build(read, mimeType, config);
    }
}
