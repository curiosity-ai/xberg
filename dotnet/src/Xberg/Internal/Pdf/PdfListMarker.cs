namespace Xberg.Internal.Pdf;

/// <summary>
/// Shared parsing for ordered-list marker syntax (ports <c>pdf/structure/list_marker.rs</c>).
/// </summary>
/// <remarks>
/// The digit and roman-numeral caps are what keep a year or a figure number from reading as a
/// list marker: <c>2023. A total of 3 trucks…</c> is prose, and only the three-digit limit
/// distinguishes it from <c>12. Second item</c>.
/// </remarks>
internal static class PdfListMarker
{
    private const int MaxNumericMarkerDigits = 3;
    private const int MaxRomanMarkerChars = 4;

    internal readonly record struct OrderedListMarker(
        int ContentStart, bool HasContent, bool HasSeparator, int? NumericValue);

    /// <summary>The ordered-list marker <paramref name="text"/> opens with, if any.</summary>
    public static OrderedListMarker? Parse(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0) return null;
        int leadingWhitespace = text.Length - trimmed.Length;

        var parsed = ParseBracketedNumericMarker(trimmed)
            ?? ParseParenthesizedMarker(trimmed)
            ?? ParseSuffixedMarker(trimmed);
        if (parsed is not { } m) return null;

        return FinishMarker(text, leadingWhitespace + m.MarkerLength, m.NumericValue);
    }

    private static (int MarkerLength, int? NumericValue)? ParseBracketedNumericMarker(string text)
    {
        if (!text.StartsWith('[')) return null;
        int closing = text.IndexOf(']', 1);
        if (closing < 0) return null;
        int? value = NumericMarkerValue(text[1..closing]);
        return value is null ? null : (closing + 1, value);
    }

    private static (int MarkerLength, int? NumericValue)? ParseParenthesizedMarker(string text)
    {
        if (!text.StartsWith('(')) return null;
        int closing = text.IndexOf(')', 1);
        if (closing < 0) return null;
        string marker = text[1..closing];
        if (marker.Length == 0 || !marker.All(char.IsLetterOrDigit)) return null;
        return (closing + 1, NumericMarkerValue(marker));
    }

    private static (int MarkerLength, int? NumericValue)? ParseSuffixedMarker(string text)
    {
        int delimiter = text.IndexOfAny(['.', ')']);
        if (delimiter < 0) return null;
        string marker = text[..delimiter];
        int? value = NumericMarkerValue(marker);
        bool valid = value is not null
            || (marker.Length == 1 && char.IsLetterOrDigit(marker[0]))
            || IsRomanMarker(marker);
        return valid ? (delimiter + 1, value) : null;
    }

    private static OrderedListMarker FinishMarker(string text, int markerEnd, int? numericValue)
    {
        if (markerEnd >= text.Length)
            return new OrderedListMarker(markerEnd, false, false, numericValue);

        if (char.IsWhiteSpace(text[markerEnd]))
        {
            string content = text[markerEnd..].TrimStart();
            return new OrderedListMarker(text.Length - content.Length, content.Length != 0, true, numericValue);
        }

        return new OrderedListMarker(markerEnd, true, false, numericValue);
    }

    private static int? NumericMarkerValue(string marker)
    {
        if (marker.Length is < 1 or > MaxNumericMarkerDigits) return null;
        if (!marker.All(char.IsAsciiDigit)) return null;
        return int.TryParse(marker, out int v) ? v : null;
    }

    private static bool IsRomanMarker(string marker) =>
        marker.Length is >= 1 and <= MaxRomanMarkerChars
        && marker.All(c => char.ToLowerInvariant(c) is 'i' or 'v' or 'x' or 'l' or 'c' or 'd' or 'm');

    /// <summary>
    /// Whether a single-capital marker is more likely an author's first initial. The comma plus a
    /// second compact initial (or a journal-style slash) is the evidence; a bare
    /// <c>A. First item</c> stays a list.
    /// </summary>
    public static bool IsProbableAuthorByline(string text)
    {
        if (text.Length < 2 || !char.IsAsciiLetterUpper(text[0]) || text[1] != '.') return false;
        string remainder = text[2..].TrimStart();
        int space = remainder.IndexOfAny([' ', '\t', '\n', '\r', '\f', '\v']);
        if (space < 0) return false;
        string surname = remainder[..space];
        return surname.EndsWith(',') && StartsWithAuthorInitialOrSlash(remainder[(space + 1)..].TrimStart());
    }

    private static bool StartsWithAuthorInitialOrSlash(string text)
    {
        if (text.StartsWith('/')) return true;
        int i = 0, initials = 0;
        while (i < text.Length && char.IsAsciiLetterUpper(text[i]))
        {
            i++;
            if (i >= text.Length || text[i] != '.') return false;
            i++;
            initials++;
        }
        return initials > 0 && i < text.Length && char.IsWhiteSpace(text[i]);
    }
}
