using Xberg.Types;

namespace Xberg.Core;

/// <summary>
/// Public entry point: dispatches an <see cref="ExtractInput"/> to a registered extractor,
/// derives the <see cref="ExtractedDocument"/>, and wraps it in an <see cref="ExtractionResult"/>.
/// When no extractor is registered for a MIME type, a graceful empty result is produced.
/// </summary>
public sealed class Extractor
{
    private readonly Registry _registry;

    public Extractor() : this(Registry.RegisterDefaults()) { }

    public Extractor(Registry registry) => _registry = registry;

    public ExtractionResult Extract(ExtractInput input, ExtractionConfig config)
    {
        var result = new ExtractionResult();
        try
        {
            var (bytes, mimeType) = Resolve(input);
            var extractor = _registry.ForMime(mimeType);
            if (extractor is null)
            {
                // Graceful: return an empty document carrying the detected MIME + a warning.
                var empty = new ExtractedDocument { MimeType = mimeType };
                empty.ProcessingWarnings.Add(new ProcessingWarning
                {
                    Source = "registry",
                    Message = $"No extractor registered for MIME type: {mimeType}",
                });
                result.Results.Add(empty);
            }
            else
            {
                var internalDoc = extractor.Extract(bytes, mimeType, config);
                var extracted = Derive.DeriveExtractionResult(internalDoc, config.IncludeDocumentStructure, config.OutputFormat);
                // Record the format the content was rendered in (Rust `pipeline::format`).
                // Set after derive so the extractor-supplied value the renderer consults for
                // `PreRenderedContent` is not disturbed.
                extracted.Metadata.OutputFormat = config.OutputFormat.ToString();
                result.Results.Add(extracted);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add(new ExtractionErrorItem
            {
                Index = 0,
                Code = 1,
                ErrorType = "extraction_error",
                Source = input.Filename ?? input.Uri ?? "input",
                Message = ex.Message,
            });
        }

        result.Summary = new ExtractionSummary
        {
            Inputs = 1,
            Results = result.Results.Count,
            Errors = result.Errors.Count,
        };
        return result;
    }

    public Task<ExtractionResult> ExtractAsync(ExtractInput input, ExtractionConfig config) =>
        Task.FromResult(Extract(input, config));

    private static (byte[] Bytes, string MimeType) Resolve(ExtractInput input)
    {
        if (input.Kind == ExtractInputKind.Bytes)
        {
            byte[] bytes = input.Bytes ?? Array.Empty<byte>();
            string mime = input.MimeType
                ?? Mime.DetectMimeTypeFromBytes(bytes)
                ?? Mime.OctetStream;
            return (bytes, mime);
        }

        // URI / local path.
        string uri = input.Uri ?? throw new InvalidOperationException("ExtractInput.Uri is null");
        string path = uri.StartsWith("file://", StringComparison.Ordinal) ? new Uri(uri).LocalPath : uri;
        byte[] fileBytes = File.ReadAllBytes(path);
        // A caller-supplied type is taken at its word; otherwise the extension is only a starting
        // point, and the file's own content overrules it where the two disagree.
        string mimeType = input.MimeType
            ?? Mime.ResolveWithContent(Mime.DetectMimeType(path, checkExists: false), fileBytes);
        return (fileBytes, mimeType);
    }
}
