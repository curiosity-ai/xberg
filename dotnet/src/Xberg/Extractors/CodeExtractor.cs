using System.Text;
using Xberg.Core;
using Xberg.Internal.Code;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Source-code extractor, ported from Rust <c>extractors/code.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Upstream detects the language, hands the source to <c>tree_sitter_language_pack::process</c>,
/// and emits one code element per structural chunk it comes back with — or, when it comes back
/// with none, one code element holding the source verbatim.
/// </para>
/// <para>
/// <b>The chunking half is not ported.</b> It is tree-sitter's, and tree-sitter is a C library
/// with a grammar per language; reproducing it is a different project from reproducing xberg.
/// What that costs is bounded and measurable: under the default configuration
/// <c>tslp::process</c> returns no chunks — every one of the corpus's eighteen source fixtures
/// has <c>chunks: []</c> and <c>data: null</c> in the goldens generated with the feature on —
/// so the verbatim path is the whole of the observed behaviour, and it is what this reproduces.
/// A caller who turns chunking on upstream gets headings and per-chunk code elements that this
/// port will not produce.
/// </para>
/// </remarks>
public sealed class CodeExtractor : IExtractor
{
    /// <summary>The MIME every source file resolves to, whatever language it turns out to be.</summary>
    public const string SourceCodeMimeType = "text/x-source-code";

    public IEnumerable<string> SupportedMimeTypes => new[] { SourceCodeMimeType };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        SecurityBudget.FromConfig(config).AccountText(content.Length);

        // `String::from_utf8_lossy` in Rust: invalid sequences become U+FFFD rather than an error.
        // The decoder is asked whether it had to substitute anything, because upstream warns when
        // it did — a source file that needed replacing is one whose text is not what was written.
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true);
        string source;
        bool decodedLossily = false;
        try
        {
            source = decoder.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            source = Encoding.UTF8.GetString(content);
            decodedLossily = true;
        }

        // Content before name: a shebang is what the file says about itself, and it outranks
        // whatever the extension claims.
        string? language = CodeLanguages.FromContent(source)
            ?? (config.SourceName is { } name ? CodeLanguages.FromPath(name) : null);

        if (language is null)
            throw new NotSupportedException(
                "Cannot detect programming language from content (no shebang line). "
                + "Use extract_file with a file path for extension-based detection.");

        var doc = BuildDocument(source, language);
        if (decodedLossily)
            doc.ProcessingWarnings.Add(new ProcessingWarning
            {
                Source = "code",
                Message = "source file contained invalid UTF-8; undecodable bytes were replaced",
            });
        return doc;
    }

    /// <summary>Build the document for a source file whose language is already known.</summary>
    internal static InternalDocument BuildDocument(string source, string language)
    {
        var builder = new InternalDocumentBuilder("code");
        builder.PushCode(source, language, null, null);

        var doc = builder.Build();
        doc.Metadata = new Metadata { Format = FormatMetadata.Code(new CodeMetadata()) };
        doc.MimeType = SourceCodeMimeType;
        return doc;
    }
}
