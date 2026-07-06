// Ported from crates/xberg/src/extraction/email.rs — the Content-Transfer-Encoding
// handling that `mail_parser` performs internally when producing part contents.
// Native MIME parser (System.Net.Mail is insufficient).
using System.Text;

namespace Xberg.Internal.Email;

/// <summary>
/// Decodes a part body according to its <c>Content-Transfer-Encoding</c>.
/// The body arrives as a Latin-1 string (each char == one raw byte) so structural
/// parsing stays byte-exact; decoders return raw <see cref="byte"/>[].
/// </summary>
internal static class ContentTransferDecoder
{
    /// <summary>Decode the Latin-1-preserved body per the transfer encoding.</summary>
    internal static byte[] Decode(string? cte, string bodyLatin1)
    {
        string enc = (cte ?? "").Trim().ToLowerInvariant();
        return enc switch
        {
            "base64" => DecodeBase64(bodyLatin1),
            "quoted-printable" => DecodeQuotedPrintable(bodyLatin1),
            // "7bit" | "8bit" | "binary" | "" | anything else -> raw bytes.
            _ => Latin1Bytes(bodyLatin1),
        };
    }

    /// <summary>Recover the raw bytes a Latin-1 string was built from.</summary>
    internal static byte[] Latin1Bytes(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>
    /// Base64 decode, ignoring ASCII whitespace. If any non-alphabet character is present
    /// or the significant length is invalid (len % 4 == 1), the content is treated as
    /// undecodable and the raw bytes are returned — matching `mail_parser`, which returns the
    /// raw part body when base64 decoding fails.
    /// </summary>
    internal static byte[] DecodeBase64(string bodyLatin1)
    {
        var sb = new StringBuilder(bodyLatin1.Length);
        bool malformed = false;
        foreach (char c in bodyLatin1)
        {
            if (c == '\r' || c == '\n' || c == ' ' || c == '\t')
                continue;
            if (IsBase64Char(c) || c == '=')
                sb.Append(c);
            else
                malformed = true; // stray non-base64 byte (e.g. '>')
        }

        if (malformed)
            return Latin1Bytes(bodyLatin1);

        string cleaned = sb.ToString();
        // Drop trailing padding for the length check, then normalize padding.
        int dataLen = cleaned.Length;
        while (dataLen > 0 && cleaned[dataLen - 1] == '=') dataLen--;
        if (dataLen % 4 == 1)
            return Latin1Bytes(bodyLatin1);

        string data = cleaned.Substring(0, dataLen);
        int pad = (4 - data.Length % 4) % 4;
        try
        {
            return Convert.FromBase64String(data + new string('=', pad));
        }
        catch (FormatException)
        {
            return Latin1Bytes(bodyLatin1);
        }
    }

    private static bool IsBase64Char(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/';

    /// <summary>Quoted-printable decode (RFC 2045). Handles soft line breaks (<c>=</c> at EOL).</summary>
    internal static byte[] DecodeQuotedPrintable(string bodyLatin1)
    {
        var outBytes = new List<byte>(bodyLatin1.Length);
        int i = 0;
        int n = bodyLatin1.Length;
        while (i < n)
        {
            char c = bodyLatin1[i];
            if (c == '=' && i + 1 < n)
            {
                char n1 = bodyLatin1[i + 1];
                if (n1 == '\r' && i + 2 < n && bodyLatin1[i + 2] == '\n')
                {
                    i += 3; // soft break =\r\n
                    continue;
                }
                if (n1 == '\n')
                {
                    i += 2; // soft break =\n
                    continue;
                }
                if (i + 2 < n && TryHex(n1, out int hi) && TryHex(bodyLatin1[i + 2], out int lo))
                {
                    outBytes.Add((byte)((hi << 4) | lo));
                    i += 3;
                    continue;
                }
                // Malformed '=' — emit literally.
                outBytes.Add((byte)'=');
                i += 1;
                continue;
            }
            outBytes.Add((byte)(c & 0xFF));
            i += 1;
        }
        return outBytes.ToArray();
    }

    private static bool TryHex(char c, out int value)
    {
        if (c >= '0' && c <= '9') { value = c - '0'; return true; }
        if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
        if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
        value = 0;
        return false;
    }
}
