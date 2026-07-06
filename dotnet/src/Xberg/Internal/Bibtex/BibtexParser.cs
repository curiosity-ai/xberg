namespace Xberg.Internal.Bibtex;

/// <summary>A parsed BibTeX entry: type, cite key, and fields with lowercased names.</summary>
internal sealed class BibtexEntry
{
    public string EntryType = "";
    public string Key = "";
    public List<(string Name, string Value)> Fields = new();
}

/// <summary>
/// Minimal BibTeX/BibLaTeX parser covering the subset used by the extractor: `@type{key, name =
/// {value} | "value" | bare, ...}`. Field names are lowercased; @string/@comment/@preamble are
/// skipped. Mirrors the `biblatex` crate's observable output (fields exposed sorted by name).
/// </summary>
internal static class BibtexParser
{
    public static List<BibtexEntry> Parse(string src)
    {
        var entries = new List<BibtexEntry>();
        int i = 0;
        int n = src.Length;
        while (i < n)
        {
            if (src[i] != '@') { i++; continue; }
            i++; // past '@'
            int typeStart = i;
            while (i < n && (char.IsLetter(src[i]) || src[i] == '_')) i++;
            string type = src.Substring(typeStart, i - typeStart).ToLowerInvariant();
            SkipWs(src, ref i);
            if (i >= n || (src[i] != '{' && src[i] != '(')) continue;
            char open = src[i];
            char closeCh = open == '{' ? '}' : ')';
            i++; // past '{'

            if (type is "comment" or "preamble" or "string")
            {
                SkipBalanced(src, ref i, open, closeCh);
                continue;
            }

            SkipWs(src, ref i);
            int keyStart = i;
            while (i < n && src[i] != ',' && src[i] != closeCh) i++;
            string key = src.Substring(keyStart, i - keyStart).Trim();

            var entry = new BibtexEntry { EntryType = type, Key = key };
            while (i < n)
            {
                SkipWs(src, ref i);
                if (i < n && src[i] == ',') { i++; SkipWs(src, ref i); }
                if (i >= n || src[i] == closeCh) { if (i < n) i++; break; }

                int nameStart = i;
                while (i < n && src[i] != '=' && src[i] != closeCh && !char.IsWhiteSpace(src[i])) i++;
                string name = src.Substring(nameStart, i - nameStart).Trim().ToLowerInvariant();
                SkipWs(src, ref i);
                if (i >= n || src[i] != '=') { if (name.Length == 0) { if (i < n) i++; continue; } break; }
                i++; // past '='
                SkipWs(src, ref i);
                string value = ReadValue(src, ref i, closeCh);
                if (name.Length != 0) entry.Fields.Add((name, value));
            }

            entry.Fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            entries.Add(entry);
        }
        return entries;
    }

    private static string ReadValue(string src, ref int i, char closeCh)
    {
        int n = src.Length;
        if (i >= n) return "";
        if (src[i] == '{')
        {
            i++; // past '{'
            int depth = 1;
            var sb = new System.Text.StringBuilder();
            while (i < n)
            {
                char c = src[i];
                if (c == '{') { depth++; sb.Append(c); }
                else if (c == '}') { depth--; if (depth == 0) { i++; break; } sb.Append(c); }
                else sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
        if (src[i] == '"')
        {
            i++;
            int depth = 0;
            var sb = new System.Text.StringBuilder();
            while (i < n)
            {
                char c = src[i];
                if (c == '{') depth++;
                else if (c == '}') { if (depth > 0) depth--; }
                else if (c == '"' && depth == 0) { i++; break; }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }
        // bare value: until ',' or closing delimiter or whitespace-newline
        int start = i;
        while (i < n && src[i] != ',' && src[i] != closeCh) i++;
        return src.Substring(start, i - start).Trim();
    }

    private static void SkipBalanced(string src, ref int i, char open, char close)
    {
        int depth = 1;
        while (i < src.Length && depth > 0)
        {
            if (src[i] == open) depth++;
            else if (src[i] == close) depth--;
            i++;
        }
    }

    private static void SkipWs(string src, ref int i)
    {
        while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
    }
}
