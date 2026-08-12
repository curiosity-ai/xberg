using System.Text;
using System.Text.RegularExpressions;
using Xberg.Core;
using Xberg.Internal.Commonmark;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// MDX extractor with JSX stripping and frontmatter support. Ported from
/// <c>crates/xberg/src/extractors/mdx.rs</c>. Strips MDX-specific syntax (imports, exports,
/// JSX component tags, inline expressions), then processes the remainder as Markdown via the
/// shared <see cref="MarkdownExtractor.BuildInternalDocument"/> logic.
/// </summary>
public sealed class MdxExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "text/mdx", "text/x-mdx" };

    public int Priority => 50;

    private static readonly Regex JsxTagRe = new(
        @"</?[A-Z][a-zA-Z0-9_.]*(?:\s[^>]*)?>|<[A-Z][a-zA-Z0-9_.]*(?:\s[^>]*)?\s*/>", RegexOptions.Compiled);
    private static readonly Regex JsxExprLineRe = new(@"^\s*\{.*\}\s*$", RegexOptions.Compiled);
    private static readonly Regex JsxInlineCommentRe = new(@"\s*\{/\*.*?\*/\}", RegexOptions.Compiled);

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string text = Encoding.UTF8.GetString(content);
        var (yaml, remaining) = MarkdownExtractor.ExtractFrontmatter(text);

        var metadata = yaml is not null ? MarkdownExtractor.ExtractMetadataFromYaml(yaml) : new Metadata();

        var jsxBlocks = new List<string>();
        string clean = StripMdxSyntax(remaining, jsxBlocks);

        if (metadata.Title is null)
        {
            string? title = MarkdownExtractor.ExtractTitleFromContent(clean);
            if (title is not null) metadata.Title = title;
        }

        var events = MarkdownParser.Parse(clean);
        var doc = MarkdownExtractor.BuildInternalDocument(events, yaml, "mdx", jsxBlocks);
        doc.Metadata = metadata;
        doc.MimeType = mimeType;
        return doc;
    }

    private static string StripMdxSyntax(string content, List<string> jsxBlocks)
    {
        var result = new StringBuilder(content.Length);
        bool inCodeFence = false;
        int skipBlockDepth = 0;

        foreach (var line in SplitLines(content))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                result.Append(line).Append('\n');
                continue;
            }
            if (inCodeFence)
            {
                result.Append(line).Append('\n');
                continue;
            }

            if (skipBlockDepth > 0)
            {
                skipBlockDepth += CountBraces(trimmed);
                if (skipBlockDepth <= 0) skipBlockDepth = 0;
                continue;
            }
            if (trimmed.StartsWith("import ", StringComparison.Ordinal) || trimmed == "import")
            {
                int depth = CountBraces(trimmed);
                if (depth > 0) skipBlockDepth = depth;
                continue;
            }
            if (trimmed.StartsWith("export ", StringComparison.Ordinal) || trimmed == "export")
            {
                int depth = CountBraces(trimmed);
                if (depth > 0) skipBlockDepth = depth;
                continue;
            }
            if (JsxExprLineRe.IsMatch(trimmed)) continue;

            string withoutComments = JsxInlineCommentRe.Replace(line, "");
            string processed = JsxTagRe.Replace(withoutComments, "");
            string processedTrimmed = processed.Trim();

            if (processedTrimmed.Length == 0 && trimmed.Length > 0)
            {
                foreach (Match m in JsxTagRe.Matches(withoutComments))
                    jsxBlocks.Add(m.Value);
                continue;
            }

            result.Append(processed).Append('\n');
        }
        return result.ToString();
    }

    private static int CountBraces(string line)
    {
        int depth = 0;
        foreach (char c in line)
        {
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return depth;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i;
                if (end > start && text[end - 1] == '\r') end--;
                yield return text.Substring(start, end - start);
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            int end = text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            yield return text.Substring(start, end - start);
        }
    }
}
