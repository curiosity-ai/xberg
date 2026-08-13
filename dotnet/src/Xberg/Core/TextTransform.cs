using System.Text;

namespace Xberg.Core;

/// <summary>
/// Shared text transforms ported from Rust <c>extraction/transform</c>.
/// </summary>
public static class TextTransform
{
    /// <summary>
    /// Normalize CRLF and lone CR line endings to LF.
    /// <para>
    /// Call this before splitting on <c>"\n\n"</c>: a CRLF-authored document (RFC 5322 mandates
    /// CRLF for email, and Windows-authored sources are common) otherwise never matches the
    /// paragraph boundary, collapsing the whole document into one paragraph carrying stray
    /// carriage returns (Rust GH#316).
    /// </para>
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        if (!text.Contains('\r')) return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                sb.Append('\n');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
