using System.Text;
using Xberg.Types;

namespace Xberg.Internal.Markup;

/// <summary>
/// Shared helpers for the lightweight markup extractors (RST, Org, Typst, LaTeX, Jupyter).
/// Provides AnnotationKind/ExtractedUri factories mirroring the Rust helpers and a UTF-8
/// output buffer that tracks byte offsets so inline-markup annotations line up byte-for-byte
/// with the Rust extractors.
/// </summary>
internal static class MarkupHelpers
{
    public static AnnotationKind Bold => new() { Which = AnnotationKind.Tag.Bold };
    public static AnnotationKind Italic => new() { Which = AnnotationKind.Tag.Italic };
    public static AnnotationKind Underline => new() { Which = AnnotationKind.Tag.Underline };
    public static AnnotationKind Strikethrough => new() { Which = AnnotationKind.Tag.Strikethrough };
    public static AnnotationKind Code => new() { Which = AnnotationKind.Tag.Code };
    public static AnnotationKind Link(string url, string? title) =>
        new() { Which = AnnotationKind.Tag.Link, Url = url, Title = title };

    public static UriKind ClassifyUri(string url)
    {
        if (url.StartsWith("mailto:", StringComparison.Ordinal)) return UriKind.Email;
        if (url.StartsWith("#", StringComparison.Ordinal)) return UriKind.Anchor;
        return UriKind.Hyperlink;
    }

    public static ExtractedUri Hyperlink(string url, string? label) =>
        new() { Url = url, Label = label, Page = null, Kind = ClassifyUri(url) };

    public static ExtractedUri Image(string url, string? label) =>
        new() { Url = url, Label = label, Page = null, Kind = UriKind.Image };

    public static ExtractedUri Citation(string url, string? label) =>
        new() { Url = url, Label = label, Page = null, Kind = UriKind.Citation };

    public static TextAnnotation Annotation(uint start, uint end, AnnotationKind kind) =>
        new() { Start = start, End = end, Kind = kind };

    /// <summary>Splits like Rust `str::lines()`: on '\n', stripping a trailing '\r',
    /// with no empty trailing line for a final newline.</summary>
    public static List<string> Lines(string s)
    {
        var res = new List<string>();
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                string line = s.Substring(start, i - start);
                if (line.EndsWith('\r')) line = line[..^1];
                res.Add(line);
                start = i + 1;
            }
        }
        if (start < s.Length)
        {
            string line = s.Substring(start);
            if (line.EndsWith('\r')) line = line[..^1];
            res.Add(line);
        }
        return res;
    }

    /// <summary>Rust `str::trim()` — trims Unicode whitespace from both ends.</summary>
    public static string Trim(string s) => s.Trim();
}

/// <summary>
/// A growable output buffer over UTF-8 bytes. Byte offsets returned by <see cref="Len"/>
/// match Rust `String::len()` semantics, so inline annotations computed against it are
/// byte-for-byte identical to the Rust extractors.
/// </summary>
internal sealed class Utf8Buf
{
    private readonly List<byte> _bytes = new();

    public uint Len => (uint)_bytes.Count;

    public void Append(string s)
    {
        if (s.Length == 0) return;
        _bytes.AddRange(Encoding.UTF8.GetBytes(s));
    }

    public void AppendByte(byte b) => _bytes.Add(b);

    public void AppendChar(char c) => Append(c.ToString());

    public override string ToString() => Encoding.UTF8.GetString(_bytes.ToArray());
}
