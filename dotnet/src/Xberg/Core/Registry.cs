using Xberg.Types;

namespace Xberg.Core;

/// <summary>
/// A synchronous extractor: advertises the MIME types it handles and produces an
/// <see cref="InternalDocument"/>. Mirrors the Rust `InternalDocumentExtractor`/`SyncExtractor` trait.
/// </summary>
public interface IExtractor
{
    IEnumerable<string> SupportedMimeTypes { get; }

    InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config);
}

/// <summary>Maps MIME type → extractor and dispatches by MIME.</summary>
public sealed class Registry
{
    private readonly Dictionary<string, IExtractor> _byMime = new(StringComparer.Ordinal);

    public void Register(IExtractor extractor)
    {
        foreach (var mime in extractor.SupportedMimeTypes)
            _byMime[mime] = extractor;
    }

    public IExtractor? ForMime(string mimeType) => _byMime.TryGetValue(mimeType, out var e) ? e : null;

    public IEnumerable<string> SupportedMimeTypes => _byMime.Keys;

    /// <summary>Register the built-in extractors. Only PlainText is available in the core spine.</summary>
    public static Registry RegisterDefaults()
    {
        var registry = new Registry();
        registry.Register(new Extractors.PlainTextExtractor());
        return registry;
    }
}
