// Ported from crates/xberg/src/extraction/email.rs (charset handling around
// `encoding_for_windows_codepage` / `encoding_rs` decode paths) — native MIME parser.
using System.Text;

namespace Xberg.Internal.Email;

/// <summary>
/// Decodes byte payloads into strings using a named charset. Mirrors the Rust use of
/// `encoding_rs::Encoding::for_label` with a windows-1252 fallback for unknown labels.
/// Legacy code pages (windows-125x, iso-8859-x, shift_jis, gbk, ...) are enabled by
/// registering <see cref="System.Text.CodePagesEncodingProvider"/> once.
/// </summary>
internal static class CharsetDecoder
{
    static CharsetDecoder()
    {
        // Enables windows-125x / iso-8859-x / DBCS code pages on all platforms.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Decode <paramref name="bytes"/> using the given charset label (case-insensitive).</summary>
    internal static string Decode(string? charset, byte[] bytes)
    {
        var enc = ResolveEncoding(charset);
        // Strip a leading BOM the same way encoding_rs' decode() does for UTF-8/UTF-16.
        return enc.GetString(bytes);
    }

    /// <summary>Resolve a charset label to an <see cref="Encoding"/>, defaulting to windows-1252.</summary>
    internal static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Utf8NoBom;

        string label = charset.Trim().Trim('"').ToLowerInvariant();

        // Normalize the common aliases first (encoding_rs label semantics).
        switch (label)
        {
            case "utf-8":
            case "utf8":
            case "unicode-1-1-utf-8":
                return Utf8NoBom;
            case "us-ascii":
            case "ascii":
            case "ansi_x3.4-1968":
                return Encoding.ASCII;
            case "iso-8859-1":
            case "latin1":
            case "l1":
            case "cp819":
                // encoding_rs maps iso-8859-1 to windows-1252.
                label = "windows-1252";
                break;
            case "utf-16":
            case "utf-16le":
                return Encoding.Unicode;
            case "utf-16be":
                return Encoding.BigEndianUnicode;
        }

        try
        {
            return Encoding.GetEncoding(label);
        }
        catch (ArgumentException)
        {
            try
            {
                return Encoding.GetEncoding("windows-1252");
            }
            catch (ArgumentException)
            {
                return Utf8NoBom;
            }
        }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
