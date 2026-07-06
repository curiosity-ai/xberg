// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`SevenZExtractor`).
//
// DEFERRED: the Rust path uses the `sevenz-rust2` crate. A faithful managed 7z reader
// requires a full LZMA/LZMA2/BCJ decoder (the 7z header itself is typically compressed),
// which is out of scope for this pass and has no permissive BCL equivalent. The extractor
// is registered so the MIME type is claimed, but extraction reports an unsupported error
// rather than silently returning nothing. See PORT_NOTES.

using Xberg.Core;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>7z archive extractor — decompression deferred (no managed LZMA decoder ported).</summary>
public sealed class SevenZipExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-7z-compressed" };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config) =>
        throw new NotSupportedException(
            "7z extraction is not yet ported (requires a managed LZMA/LZMA2 decoder).");
}
