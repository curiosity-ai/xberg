// Ported from Rust `crates/xberg/src/extractors/archive.rs` (`build_archive_doc_inner` +
// `build_archive_doc`). Builds the archive InternalDocument and — bounded by a recursion
// depth guard — extracts each child through the public pipeline into `Children`.

using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Types;

namespace Xberg.Internal.Archive;

internal static class ArchiveDocument
{
    /// <summary>Rust default `max_archive_depth`.</summary>
    private const int MaxArchiveDepth = 3;

    [ThreadStatic] private static int _depth;

    private readonly record struct FileDetail(string Path, ulong Size, bool IsDir);

    public static InternalDocument Build(ArchiveReadResult read, string mimeType, ExtractionConfig config)
    {
        var info = read.Info;
        string format = info.Format;

        // Recursively extract children through the public pipeline, bounded by MaxArchiveDepth.
        var children = new List<ArchiveEntry>();
        var warnings = new List<ProcessingWarning>();
        if (_depth < MaxArchiveDepth && read.FileBytes.Count > 0)
        {
            foreach (var (path, bytes) in read.FileBytes)
            {
                string? mime = DetectChildMime(path, bytes);
                if (mime is null || mime == Mime.OctetStream) continue;

                _depth++;
                try
                {
                    var result = new Extractor().Extract(ExtractInput.FromBytes(bytes, mime, path), config);
                    var childDoc = result.Results.FirstOrDefault();
                    if (result.Errors.Count > 0 || childDoc is null)
                    {
                        string msg = result.Errors.Count > 0 ? result.Errors[0].Message : "no result";
                        warnings.Add(new ProcessingWarning
                        {
                            Source = "archive_recursive_extraction",
                            Message = $"Failed to extract '{path}': {msg}",
                        });
                    }
                    else
                    {
                        children.Add(new ArchiveEntry { Path = path, MimeType = mime, Result = childDoc });
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(new ProcessingWarning
                    {
                        Source = "archive_recursive_extraction",
                        Message = $"Failed to extract '{path}': {ex.Message}",
                    });
                }
                finally
                {
                    _depth--;
                }
            }
        }

        var archiveMeta = new ArchiveMetadata
        {
            Format = format,
            FileCount = (uint)info.FileCount,
            FileList = info.FileList.Select(e => e.Path).ToList(),
            TotalSize = info.TotalSize,
            CompressedSize = null,
        };

        var files = info.FileList.Select(e => new FileDetail(e.Path, e.Size, e.IsDir)).ToList();
        var additional = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["files"] = JsonSerializer.SerializeToElement(files, Json.Options),
        };

        var builder = new InternalDocumentBuilder(format.ToLowerInvariant());
        builder.SetMimeType(mimeType);
        builder.SetMetadata(new Metadata
        {
            Format = new FormatMetadata { FormatType = "archive", Payload = archiveMeta },
            Additional = additional,
        });

        // Archive summary paragraph.
        string summary = $"{format} Archive ({info.FileCount} files, {info.TotalSize} bytes)";
        builder.PushParagraph(summary, new(), null, null);

        // File listing paragraph.
        var fileList = new StringBuilder("Files:\n");
        foreach (var entry in info.FileList)
            fileList.Append("- ").Append(entry.Path).Append(" (").Append(entry.Size).Append(" bytes)\n");
        builder.PushParagraph(fileList.ToString(), new(), null, null);

        // Text file contents.
        foreach (var (path, content) in read.TextContents)
            builder.PushParagraph($"=== {path} ===\n{content}", new(), null, null);

        var doc = builder.Build();
        doc.Children = children.Count == 0 ? null : children;
        foreach (var w in warnings) doc.ProcessingWarnings.Add(w);
        return doc;
    }

    private static string? DetectChildMime(string path, byte[] bytes)
    {
        string? detected = Mime.DetectMimeTypeFromBytes(bytes);
        if (detected is not null && detected != Mime.OctetStream) return detected;
        return Mime.DetectMimeType(path, checkExists: false);
    }
}
