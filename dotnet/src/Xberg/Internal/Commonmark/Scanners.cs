namespace Xberg.Internal.Commonmark;

/// <summary>Minimal ports of the comrak scanners needed by the formatters.</summary>
internal static class Scanners
{
    /// <summary>
    /// Ported from <c>scanners::scheme</c>: matches <c>[A-Za-z][A-Za-z0-9.+-]{1,31}:</c> at the
    /// start of the string. Returns the byte index just past the <c>:</c>, or null.
    /// </summary>
    public static int? Scheme(string s)
    {
        if (s.Length == 0) return null;
        char c0 = s[0];
        bool alpha = (c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z');
        if (!alpha) return null;

        int i = 1;
        int rest = 0;
        while (i < s.Length && rest < 31)
        {
            char c = s[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                      || c == '.' || c == '+' || c == '-';
            if (!ok) break;
            i++;
            rest++;
        }
        // Need at least 1 (min {1,31}) scheme char after the first letter, then ':'.
        if (rest < 1) return null;
        if (i < s.Length && s[i] == ':') return i + 1;
        return null;
    }
}
