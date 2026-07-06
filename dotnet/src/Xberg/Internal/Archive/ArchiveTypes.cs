// Ported from Rust `crates/xberg/src/extraction/archive/mod.rs`.
// Shared archive model types + the text-file extension allow-list.

namespace Xberg.Internal.Archive;

/// <summary>Information about a single file in an archive (Rust `ArchiveEntry`).</summary>
internal readonly record struct ArchiveFileEntry(string Path, ulong Size, bool IsDir);

/// <summary>Archive metadata extracted from an archive file (Rust `ArchiveMetadata`).</summary>
internal sealed class ArchiveInfo
{
    public string Format { get; set; } = "";
    public List<ArchiveFileEntry> FileList { get; set; } = new();
    public int FileCount { get; set; }
    public ulong TotalSize { get; set; }
}

/// <summary>The three parallel maps produced by an archive read pass.</summary>
/// <remarks>
/// <see cref="TextContents"/> and <see cref="FileBytes"/> preserve the archive's
/// stored order (a <see cref="List{T}"/> of key/value pairs) rather than a hash map;
/// the Rust source uses an <c>AHashMap</c> whose iteration order is nondeterministic,
/// so byte-exact <c>plain</c>/<c>json</c> parity on multi-text-file archives is not
/// achievable — see PORT_NOTES.
/// </remarks>
internal sealed class ArchiveReadResult
{
    public ArchiveInfo Info { get; set; } = new();
    public List<KeyValuePair<string, string>> TextContents { get; set; } = new();
    public List<KeyValuePair<string, byte[]>> FileBytes { get; set; } = new();
}

internal static class ArchiveConstants
{
    /// <summary>Common text file extensions that are extracted from archives.</summary>
    public static readonly string[] TextExtensions =
        { ".txt", ".md", ".json", ".xml", ".html", ".csv", ".log", ".yaml", ".toml" };

    public static bool IsTextFile(string path)
    {
        string lower = path.ToLowerInvariant();
        foreach (var ext in TextExtensions)
            if (lower.EndsWith(ext, StringComparison.Ordinal))
                return true;
        return false;
    }
}
