namespace Xberg.Internal.DocTags;

/// <summary>What a DocTags token is.</summary>
internal enum DocTagKind { Open, Close, Text }

/// <summary>One lexical unit of a DocTags stream.</summary>
internal readonly record struct DocTagToken(DocTagKind Kind, string Value);

/// <summary>
/// The DocTags vocabulary and tokenizer, shared by the parser and the renderer.
/// </summary>
/// <remarks>
/// <b>There is no escaping in this format.</b> Real Docling output carries a literal <c>&lt;</c>
/// inside prose — a caption in the vendored <c>2203.01017v2</c> corpus discusses
/// <c>' &lt; td &gt; '</c> — so scanning to the next <c>&gt;</c> would swallow the
/// <c>&lt;/caption&gt;</c> that follows. Only <em>recognised</em> tag names are treated as tags
/// and everything else is content, which is how Docling's own tokenizer behaves.
/// </remarks>
internal static class DocTagsVocabulary
{
    /// <summary>DocTags normalises bounding boxes onto a fixed square grid of this size.</summary>
    public const double LocGrid = 500.0;

    /// <summary>Cell tokens that occupy one OTSL grid position.</summary>
    public static readonly string[] OtslCells =
        { "fcel", "ched", "ecel", "lcel", "ucel", "xcel", "rhed" };

    /// <summary>Tokens that stand alone rather than wrapping content.</summary>
    public static readonly string[] Standalone = { "nl", "page_break" };

    /// <summary>
    /// Tags that wrap content and must be closed. <c>checkbox_*</c> wrap their label in real
    /// Docling output, e.g. <c>&lt;checkbox_unselected&gt;&lt;loc_…&gt;بلی&lt;/checkbox_unselected&gt;</c>.
    /// </summary>
    public static readonly string[] Paired =
    {
        "doctag", "checkbox_selected", "checkbox_unselected", "text", "title", "page_header",
        "page_footer", "footnote", "caption", "code", "formula", "otsl", "picture", "list_item",
        "ordered_list", "unordered_list",
    };

    public static bool IsStandalone(string name) =>
        Array.IndexOf(Standalone, name) >= 0
        || Array.IndexOf(OtslCells, name) >= 0
        || name.StartsWith("loc_", StringComparison.Ordinal)
        // Code language tokens, e.g. `<_rust_>` and `<_unknown_>`.
        || (name.Length > 1 && name[0] == '_' && name[^1] == '_');

    public static bool IsPaired(string name) =>
        Array.IndexOf(Paired, name) >= 0
        || name.StartsWith("section_header_level_", StringComparison.Ordinal);

    /// <summary>
    /// Split a stream into tags and content. Anything that is not a recognised tag stays
    /// content, including a stray <c>&lt;</c>.
    /// </summary>
    public static List<DocTagToken> Tokenize(string input)
    {
        var output = new List<DocTagToken>();
        int cursor = 0;
        int textStart = 0;

        while (cursor < input.Length)
        {
            if (input[cursor] != '<') { cursor++; continue; }

            int end = input.IndexOf('>', cursor);
            if (end < 0) break;

            string raw = input[(cursor + 1)..end];
            bool closing = raw.StartsWith('/');
            string name = closing ? raw[1..] : raw;

            if (!IsStandalone(name) && !IsPaired(name)) { cursor++; continue; }

            if (textStart < cursor)
                output.Add(new DocTagToken(DocTagKind.Text, input[textStart..cursor]));

            output.Add(new DocTagToken(closing ? DocTagKind.Close : DocTagKind.Open, name));
            cursor = end + 1;
            textStart = cursor;
        }

        if (textStart < input.Length)
            output.Add(new DocTagToken(DocTagKind.Text, input[textStart..]));

        return output;
    }
}
