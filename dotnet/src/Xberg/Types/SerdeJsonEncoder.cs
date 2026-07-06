using System.Text.Encodings.Web;

namespace Xberg.Types;

/// <summary>
/// A <see cref="JavaScriptEncoder"/> that reproduces <c>serde_json</c>'s escaping exactly:
/// only the JSON-mandatory characters are escaped — <c>"</c>, <c>\</c>, and the C0 control
/// range (U+0000–U+001F, using the short forms <c>\b \t \n \f \r</c> and <c>\u00XX</c> otherwise).
/// Everything else — <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, U+2028/U+2029, non-ASCII, and
/// supplementary-plane scalars — is emitted as raw UTF-8, matching serde. This is what the
/// built-in <c>UnsafeRelaxedJsonEscaping</c> does <em>not</em> do (it still escapes
/// U+2028/U+2029 and emits supplementary scalars as surrogate-pair escapes).
/// </summary>
public sealed class SerdeJsonEncoder : JavaScriptEncoder
{
    public static readonly SerdeJsonEncoder Shared = new();

    public override int MaxOutputCharactersPerInputCharacter => 6; // "\u00XX"

    public override bool WillEncode(int unicodeScalar) => NeedsEscape(unicodeScalar);

    private static bool NeedsEscape(int c) => c < 0x20 || c == '"' || c == '\\';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (int i = 0; i < textLength; i++)
            if (NeedsEscape(text[i]))
                return i;
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(
        int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        string? escaped = unicodeScalar switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            0x08 => "\\b",
            0x09 => "\\t",
            0x0A => "\\n",
            0x0C => "\\f",
            0x0D => "\\r",
            _ when unicodeScalar < 0x20 => "\\u" + unicodeScalar.ToString("x4"),
            _ => null,
        };

        if (escaped is null)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        if (escaped.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        for (int i = 0; i < escaped.Length; i++)
            buffer[i] = escaped[i];
        numberOfCharactersWritten = escaped.Length;
        return true;
    }
}
