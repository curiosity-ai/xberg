using Xberg.Core;
using Xberg.Internal.Dbf;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// dBASE (.dbf) extractor. Ported from Rust `extractors/dbf.rs`. Reads records into a single
/// table (header row + data rows). The C# <see cref="DbfMetadata"/> payload is a stub without the
/// per-field descriptor list, so metadata carries record/field counts only (documented gap).
/// </summary>
public sealed class DbfExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/x-dbf", "application/dbase" };
    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var parsed = DbfReader.Parse(content);

        var builder = new InternalDocumentBuilder("dbf");
        if (parsed.FieldNames.Count > 0)
        {
            var tableRows = new List<List<string>>(parsed.Rows.Count + 1) { new(parsed.FieldNames) };
            tableRows.AddRange(parsed.Rows.Select(r => new List<string>(r)));
            builder.PushTableFromCells(tableRows, null, null);
        }

        var doc = builder.Build();
        doc.MimeType = mimeType;
        doc.Metadata = new Metadata
        {
            Format = new FormatMetadata
            {
                FormatType = "dbf",
                Payload = new DbfMetadata { RecordCount = parsed.RecordCount, FieldCount = parsed.FieldNames.Count },
            },
        };
        return doc;
    }
}
