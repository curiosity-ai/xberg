using System.IO.Compression;
using Xberg.Core;
using Xberg.Extractors;
using Xunit;

namespace Xberg.Tests;

/// <summary>Covers the document properties an ODF spreadsheet carries outside its sheets.</summary>
public class OdsMetadataTests
{
    private const string MetaXml =
        "<?xml version=\"1.0\"?><office:document-meta " +
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><office:meta>" +
        "<meta:initial-creator>Ada</meta:initial-creator>" +
        "<dc:creator>Grace</dc:creator>" +
        "<meta:creation-date>2024-11-16T05:17:41</meta:creation-date>" +
        "<dc:date>2025-01-24T13:18:51</dc:date>" +
        "</office:meta></office:document-meta>";

    private const string ContentXml =
        "<?xml version=\"1.0\"?><office:document-content " +
        "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
        "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
        "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\">" +
        "<office:body><office:spreadsheet><table:table table:name=\"Sheet1\">" +
        "<table:table-row><table:table-cell office:value-type=\"string\">" +
        "<text:p>hello</text:p></table:table-cell></table:table-row>" +
        "</table:table></office:spreadsheet></office:body></office:document-content>";

    private static byte[] BuildOds()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in new[] { ("meta.xml", MetaXml), ("content.xml", ContentXml) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(body);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// ODF splits authorship the other way round from OOXML: `meta:initial-creator` is who made
    /// the document and `dc:creator` is who last touched it. Mapping by tag name rather than by
    /// role puts the wrong person in every field.
    /// </summary>
    [Fact]
    public void AuthorshipIsMappedByRoleNotByTagName()
    {
        var doc = new OdsExtractor().Extract(
            BuildOds(), "application/vnd.oasis.opendocument.spreadsheet", new ExtractionConfig());

        Assert.Equal("Ada", doc.Metadata.CreatedBy);
        Assert.Equal("Grace", doc.Metadata.ModifiedBy);
        Assert.Equal(new[] { "Ada" }, doc.Metadata.Authors);
        Assert.Equal("2024-11-16T05:17:41", doc.Metadata.CreatedAt);
        Assert.Equal("2025-01-24T13:18:51", doc.Metadata.ModifiedAt);
    }
}
