using System.Reflection;
using Xberg.Types;

namespace Xberg.Core;

/// <summary>
/// A synchronous extractor: advertises the MIME types it handles and produces an
/// <see cref="InternalDocument"/>. Mirrors the Rust `InternalDocumentExtractor`/`SyncExtractor` trait.
///
/// Implementations with a public parameterless constructor are auto-discovered by
/// <see cref="Registry.RegisterDefaults"/> — no central registration list to edit.
/// </summary>
public interface IExtractor
{
    IEnumerable<string> SupportedMimeTypes { get; }

    /// <summary>Dispatch priority; higher wins when multiple extractors claim a MIME type
    /// (mirrors the Rust `priority()`). Default 50, matching the Rust default extractors.</summary>
    int Priority => 50;

    InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config);
}

/// <summary>Maps MIME type → extractor and dispatches by MIME.</summary>
public sealed class Registry
{
    private readonly Dictionary<string, IExtractor> _byMime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _priority = new(StringComparer.Ordinal);

    /// <summary>Register an extractor for all its MIME types. Higher <see cref="IExtractor.Priority"/>
    /// wins on conflict.</summary>
    public void Register(IExtractor extractor)
    {
        foreach (var mime in extractor.SupportedMimeTypes)
        {
            if (_priority.TryGetValue(mime, out var existing) && existing >= extractor.Priority)
                continue;
            _byMime[mime] = extractor;
            _priority[mime] = extractor.Priority;
        }
    }

    public IExtractor? ForMime(string mimeType) => _byMime.TryGetValue(mimeType, out var e) ? e : null;

    public IEnumerable<string> SupportedMimeTypes => _byMime.Keys;

    /// <summary>
    /// Register the built-in extractors by scanning this assembly for every concrete
    /// <see cref="IExtractor"/> with a public parameterless constructor. New format extractors
    /// are picked up automatically just by existing — no edit to this method required.
    /// </summary>
    public static Registry RegisterDefaults()
    {
        var registry = new Registry();
        foreach (var type in typeof(Registry).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract) continue;
            if (!typeof(IExtractor).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            if (Activator.CreateInstance(type) is IExtractor extractor)
                registry.Register(extractor);
        }
        return registry;
    }
}
