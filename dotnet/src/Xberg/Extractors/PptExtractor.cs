using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Cfb;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Native PowerPoint 97-2003 binary (.ppt) extractor. Ports <c>extractors/ppt.rs</c> +
/// <c>extraction/ppt/mod.rs</c>: opens the OLE/CFB container, reads the "PowerPoint Document"
/// stream, and scans record headers for TextCharsAtom (UTF-16LE) / TextBytesAtom (CP1252) text,
/// grouping by SlideListWithText and Notes containers (master slides skipped).
/// </summary>
public sealed class PptExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/vnd.ms-powerpoint" };
    public int Priority => 60;

    private const int RtTextCharsAtom = 0x0FA0;
    private const int RtTextBytesAtom = 0x0FA8;
    private const int RtSlideListWithText = 0x0FF0;
    private const int RtMainMaster = 0x03F8;
    private const int RtNotes = 0x03F0;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var comp = CompoundFile.Open(content);

        var meta = new OleUtil.OleMetadata();
        if (comp.TryReadStream("/\x05SummaryInformation") is { } si) OleUtil.ParseSummaryInfo(si, meta);

        byte[] ppt = comp.TryReadStream("/PowerPoint Document")
            ?? throw new InvalidDataException("Failed to open stream 'PowerPoint Document'");
        if (ppt.Length == 0) throw new InvalidDataException("PowerPoint Document stream is empty");

        var (texts, slideCount, speakerNotes) = ExtractTexts(ppt);
        string text = string.Join("\n\n", texts.Select(t => t).Where(t => t.Trim().Length > 0)).Trim();

        var doc = BuildInternalDocument(text, speakerNotes);
        doc.MimeType = mimeType;

        var additional = new Dictionary<string, JsonElement>
        {
            ["slide_count"] = JsonNum(slideCount),
            ["extraction_method"] = JsonStr("native_ole"),
        };
        if (speakerNotes.Count > 0)
            additional["speaker_notes"] = JsonArr(speakerNotes);

        PageStructure? pages = slideCount > 0 ? new PageStructure
        {
            TotalCount = (uint)slideCount,
            UnitType = PageUnitType.Slide,
            Pages = Enumerable.Range(1, slideCount).Select(n => (object)new PageInfoDto((uint)n)).ToList(),
        } : null;

        List<string>? authors = meta.Author is { } a ? new List<string> { a } : null;
        doc.Metadata = new Metadata
        {
            Title = meta.Title,
            Subject = meta.Subject,
            Authors = authors,
            CreatedBy = meta.Author,
            ModifiedBy = meta.LastAuthor,
            Pages = pages,
            Additional = additional,
        };
        return doc;
    }

    private sealed record PageInfoDto(uint Number);

    private static InternalDocument BuildInternalDocument(string text, List<string> speakerNotes)
    {
        var b = new InternalDocumentBuilder("ppt");
        var blocks = text.Split("\n\n");
        for (int i = 0; i < blocks.Length; i++)
        {
            string trimmed = blocks[i].Trim();
            if (trimmed.Length == 0) continue;
            uint slideNum = (uint)(i + 1);

            var lines = trimmed.Split('\n');
            string firstLine = lines.Length > 0 ? lines[0] : "";
            bool hasMore = lines.Length > 1;
            string? title = firstLine.Length <= 80 && hasMore ? firstLine : null;
            b.PushSlide(slideNum, title, null);

            if (title is not null)
            {
                for (int li = 1; li < lines.Length; li++)
                {
                    string lt = lines[li].Trim();
                    if (lt.Length > 0) b.PushParagraph(lt, new(), null, null);
                }
            }
            else
            {
                b.PushParagraph(trimmed, new(), null, null);
            }

            if (i < speakerNotes.Count && speakerNotes[i].Length > 0)
                b.PushFootnoteDefinition(speakerNotes[i], $"slide-{slideNum}-notes", null);
        }
        return b.Build();
    }

    private static (List<string> Texts, int SlideCount, List<string> Notes) ExtractTexts(byte[] data)
    {
        var texts = new List<string>();
        int slideCount = 0;
        int pos = 0;
        bool inSlideText = false;
        var currentSlide = new List<string>();
        var notes = new List<string>();
        bool inNotes = false;
        var currentNotes = new List<string>();

        while (pos + 8 <= data.Length)
        {
            int recVerInstance = OleUtil.U16(data, pos);
            int recVer = recVerInstance & 0x000F;
            int recType = OleUtil.U16(data, pos + 2);
            long recLen = OleUtil.U32(data, pos + 4);
            if (recLen > data.Length - pos) break;

            bool isContainer = recVer == 0x0F;
            int contentStart = pos + 8;
            int contentEnd = contentStart + (int)recLen;

            if (recType == RtSlideListWithText)
            {
                if (inSlideText && currentSlide.Count > 0) { texts.Add(string.Join("\n", currentSlide)); currentSlide.Clear(); }
                inSlideText = true;
                slideCount++;
                pos += 8;
                continue;
            }
            if (recType == RtNotes)
            {
                if (inNotes && currentNotes.Count > 0)
                {
                    string nt = string.Join("\n", currentNotes).Trim();
                    if (nt.Length > 0) notes.Add(nt);
                    currentNotes.Clear();
                }
                inNotes = true;
                pos += 8;
                continue;
            }
            if (recType == RtMainMaster)
            {
                pos = contentEnd;
                continue;
            }
            if (recType == RtTextCharsAtom)
            {
                if (contentEnd <= data.Length)
                {
                    var sb = new StringBuilder();
                    for (int k = contentStart; k + 1 < contentEnd; k += 2)
                        sb.Append((char)(ushort)(data[k] | (data[k + 1] << 8)));
                    string cleaned = CleanPptText(sb.ToString());
                    AddText(cleaned, inNotes, inSlideText, currentNotes, currentSlide, texts);
                }
                pos = contentEnd;
                continue;
            }
            if (recType == RtTextBytesAtom)
            {
                if (contentEnd <= data.Length)
                {
                    var sb = new StringBuilder(contentEnd - contentStart);
                    for (int k = contentStart; k < contentEnd; k++) sb.Append(OleUtil.Cp1252ToChar(data[k]));
                    string cleaned = CleanPptText(sb.ToString());
                    AddText(cleaned, inNotes, inSlideText, currentNotes, currentSlide, texts);
                }
                pos = contentEnd;
                continue;
            }

            pos = isContainer ? pos + 8 : contentEnd;
        }

        if (currentSlide.Count > 0) texts.Add(string.Join("\n", currentSlide));
        if (currentNotes.Count > 0)
        {
            string nt = string.Join("\n", currentNotes).Trim();
            if (nt.Length > 0) notes.Add(nt);
        }
        if (slideCount == 0 && texts.Count > 0) slideCount = 1;
        return (texts, slideCount, notes);
    }

    private static void AddText(string cleaned, bool inNotes, bool inSlideText,
        List<string> currentNotes, List<string> currentSlide, List<string> texts)
    {
        if (cleaned.Length == 0) return;
        if (inNotes) currentNotes.Add(cleaned);
        if (inSlideText) currentSlide.Add(cleaned);
        else if (!inNotes) texts.Add(cleaned);
    }

    private static string CleanPptText(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\r' || c == '\x0B') result.Append('\n');
            else if (c < '\x20' && c != '\n' && c != '\t') { }
            else result.Append(c);
        }
        // Trim trailing whitespace from each line.
        string cleaned = string.Join("\n", result.ToString().Split('\n').Select(l => l.TrimEnd()));
        string trimmed = cleaned.Trim();
        if (trimmed.All(c => c == '*' || c == '\n' || char.IsWhiteSpace(c))) return "";
        return cleaned;
    }

    private static JsonElement JsonStr(string s) => JsonDocument.Parse(JsonSerializer.Serialize(s)).RootElement.Clone();
    private static JsonElement JsonNum(int n) => JsonDocument.Parse(n.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement.Clone();
    private static JsonElement JsonArr(List<string> items) => JsonDocument.Parse(JsonSerializer.Serialize(items)).RootElement.Clone();
}
