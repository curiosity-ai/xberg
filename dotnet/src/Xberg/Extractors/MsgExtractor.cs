using System.Text;
using System.Text.Json;
using Xberg.Core;
using Xberg.Internal.Cfb;
using Xberg.Internal.Email;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// Outlook message (.msg) extractor. Ports the MSG branch of <c>extraction/email.rs</c>
/// (<c>parse_msg_content</c> / <c>extract_msg_from_cfb</c>) plus the shared email
/// <c>build_internal_document</c> from <c>extractors/email.rs</c>: reads MAPI property streams from
/// the OLE/CFB container (subject, sender, body, recipients, attachments), decompresses
/// PR_RTF_COMPRESSED when there is no plain/HTML body, and builds the same header-block + body +
/// attachments document the .eml path produces.
///
/// Advertises <c>application/vnd.ms-outlook</c> so it does not collide with the .eml
/// <see cref="EmailExtractor"/> (which advertises only <c>message/rfc822</c>).
/// </summary>
public sealed class MsgExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[] { "application/vnd.ms-outlook" };
    public int Priority => 60;

    private static readonly HashSet<string> EmailStructKeys = new(StringComparer.Ordinal)
    {
        "from_email", "from_name", "to_emails", "cc_emails", "bcc_emails", "message_id",
        "attachments", "subject", "date", "email_from", "email_to", "email_cc", "email_bcc",
    };

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        var comp = CompoundFile.Open(content);
        EmailExtractionResult result = ExtractMsgFromCfb(comp);

        var additional = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in result.Metadata)
            if (!EmailStructKeys.Contains(key))
                additional[key] = JsonSerializer.SerializeToElement(value);

        InternalDocument doc = BuildInternalDocument(result);
        doc.MimeType = mimeType;

        string? fromName = result.Metadata.TryGetValue("from_name", out var fn) ? fn : null;
        var emailMetadata = new EmailMetadata
        {
            FromEmail = result.FromEmail,
            FromName = fromName,
            ToEmails = result.ToEmails,
            CcEmails = result.CcEmails,
            BccEmails = result.BccEmails,
            MessageId = result.MessageId,
            Attachments = result.Attachments
                .Select(a => a.Filename ?? a.Name)
                .Where(n => n is not null).Select(n => n!).ToList(),
        };
        List<string>? authors = !string.IsNullOrEmpty(fromName) ? new List<string> { fromName! } : null;

        doc.Metadata = new Metadata
        {
            Format = new FormatMetadata { FormatType = "email", Payload = emailMetadata },
            Subject = result.Subject,
            Authors = authors,
            CreatedAt = result.Date,
            Additional = additional,
        };
        return doc;
    }

    // ── shared email document build (extractors/email.rs) ────────────────────────
    private static InternalDocument BuildInternalDocument(EmailExtractionResult result)
    {
        var builder = new InternalDocumentBuilder("email");

        // Same header set and order as the EML path (Rust `build_email_text_output`).
        var headerEntries = new List<(string, string)>();
        void AddMeta(string label, string key)
        {
            if (result.Metadata.TryGetValue(key, out var v)) headerEntries.Add((label, v));
        }

        if (result.Subject is { } subject) headerEntries.Add(("Subject", subject));
        if (result.FromEmail is { } from) headerEntries.Add(("From", from));
        if (result.ToEmails.Count > 0) headerEntries.Add(("To", string.Join(", ", result.ToEmails)));
        if (result.CcEmails.Count > 0) headerEntries.Add(("CC", string.Join(", ", result.CcEmails)));
        if (result.BccEmails.Count > 0) headerEntries.Add(("BCC", string.Join(", ", result.BccEmails)));
        AddMeta("Reply-To", "reply_to");
        if (result.Date is { } date) headerEntries.Add(("Date", date));
        if (result.MessageId is { } messageId) headerEntries.Add(("Message-ID", messageId));
        AddMeta("In-Reply-To", "in_reply_to");
        AddMeta("References", "references");
        AddMeta("List-Id", "list_id");
        AddMeta("List-Unsubscribe", "list_unsubscribe");
        if (headerEntries.Count > 0) builder.PushMetadataBlock(headerEntries, null);

        if (result.HtmlContent is { } html)
        {
            DocumentStructure htmlDoc = HtmlStructure.Build(html);
            for (int idx = 0; idx < htmlDoc.Nodes.Count; idx++)
                if (htmlDoc.Nodes[idx].Parent is null)
                    ProcessNode(htmlDoc, idx, builder);
        }
        else
        {
            foreach (string paragraph in TextTransform.NormalizeLineEndings(result.Content).Split("\n\n"))
            {
                string trimmed = paragraph.Trim();
                if (trimmed.Length > 0) builder.PushParagraph(trimmed, new(), null, null);
            }
        }

        if (result.Attachments.Count > 0)
        {
            builder.PushParagraph("Attachments:", new(), null, null);
            foreach (var att in result.Attachments)
            {
                string name = att.Filename ?? att.Name ?? "unnamed";
                int size = att.Size ?? 0;
                builder.PushParagraph($"  {name} ({size}B)", new(), null, null);
            }
        }
        return builder.Build();
    }

    private static void ProcessNode(DocumentStructure doc, int nodeIdx, InternalDocumentBuilder builder)
    {
        if (nodeIdx < 0 || nodeIdx >= doc.Nodes.Count) return;
        DocumentNode node = doc.Nodes[nodeIdx];
        NodeContent c = node.Content;
        switch (c.Which)
        {
            case NodeContent.Tag.Paragraph:
                {
                    string trimmed = (c.Text ?? "").Trim();
                    if (trimmed.Length > 0) builder.PushParagraph(trimmed, node.Annotations, null, null);
                    break;
                }
            case NodeContent.Tag.Heading:
                builder.PushHeading(c.Level, c.Text ?? "", null, null);
                break;
            case NodeContent.Tag.Title:
                builder.PushTitle(c.Text ?? "", null, null);
                break;
            case NodeContent.Tag.List:
                builder.PushList(c.Ordered);
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                builder.EndList();
                break;
            case NodeContent.Tag.ListItem:
                {
                    bool ordered = false;
                    if (node.Parent is uint p && p < doc.Nodes.Count)
                    {
                        var parent = doc.Nodes[(int)p].Content;
                        if (parent.Which == NodeContent.Tag.List) ordered = parent.Ordered;
                    }
                    builder.PushListItem(c.Text ?? "", ordered, node.Annotations, null, null);
                    foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                    break;
                }
            case NodeContent.Tag.Table:
                {
                    var grid = c.Grid ?? new TableGrid();
                    int rowsN = (int)grid.Rows;
                    int colsN = (int)grid.Cols;
                    var rows = new List<List<string>>(rowsN);
                    for (int r = 0; r < rowsN; r++)
                    {
                        var row = new List<string>(colsN);
                        for (int col = 0; col < colsN; col++) row.Add("");
                        rows.Add(row);
                    }
                    foreach (var cell in grid.Cells)
                        if (cell.Row < rowsN && colsN > 0 && cell.Col < colsN)
                            rows[(int)cell.Row][(int)cell.Col] = cell.Content;
                    builder.PushTableFromCells(rows, null, null);
                    break;
                }
            case NodeContent.Tag.Code:
                builder.PushCode(c.Text ?? "", c.Language, null, null);
                break;
            case NodeContent.Tag.Formula:
                builder.PushFormula(c.Text ?? "", null, null);
                break;
            case NodeContent.Tag.MetadataBlock:
                {
                    var entries = (c.Entries ?? new())
                        .Select(e => (e.Length > 0 ? e[0] : "", e.Length > 1 ? e[1] : "")).ToList();
                    builder.PushMetadataBlock(entries, null);
                    break;
                }
            case NodeContent.Tag.Quote:
                builder.PushQuoteStart();
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                builder.PushQuoteEnd();
                break;
            case NodeContent.Tag.Group:
                builder.PushGroupStart(c.Label, null);
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                builder.PushGroupEnd();
                break;
            case NodeContent.Tag.Admonition:
                builder.PushAdmonition(c.Kind ?? "note", c.SlideTitle, null);
                foreach (uint child in node.Children) ProcessNode(doc, (int)child, builder);
                break;
            case NodeContent.Tag.Image:
                {
                    string text = string.IsNullOrEmpty(c.Description) ? "[Image]" : c.Description!;
                    builder.PushParagraph(text, new(), null, null);
                    break;
                }
            default:
                {
                    string? text = NodeText(c);
                    if (text is not null)
                    {
                        string trimmed = text.Trim();
                        if (trimmed.Length > 0) builder.PushParagraph(trimmed, node.Annotations, null, null);
                    }
                    break;
                }
        }
    }

    private static string? NodeText(NodeContent c) => c.Which switch
    {
        NodeContent.Tag.Title => c.Text,
        NodeContent.Tag.Heading => c.Text,
        NodeContent.Tag.Paragraph => c.Text,
        NodeContent.Tag.ListItem => c.Text,
        NodeContent.Tag.Code => c.Text,
        NodeContent.Tag.Formula => c.Text,
        NodeContent.Tag.Footnote => c.Text,
        NodeContent.Tag.Citation => c.Text,
        NodeContent.Tag.RawBlock => c.RawContent,
        NodeContent.Tag.DefinitionItem => c.Term,
        _ => null,
    };

    // ── MSG / CFB extraction (extraction/email.rs) ───────────────────────────────
    private static EmailExtractionResult ExtractMsgFromCfb(CompoundFile comp)
    {
        uint? codepage = ReadIntProp(comp, "", 0x3FFD) ?? ReadIntProp(comp, "", 0x3FDE);

        string? subject = ReadStringProp(comp, "", 0x0037, codepage);
        string? senderName = ReadStringProp(comp, "", 0x0C1A, codepage);
        string? senderEmail = (ReadStringProp(comp, "", 0x0C1F, codepage) ?? ReadStringProp(comp, "", 0x0065, codepage));
        if (senderEmail is { Length: 0 }) senderEmail = null;
        string? fromEmail = senderEmail;
        string? body = ReadStringProp(comp, "", 0x1000, codepage);
        string? htmlBody = ReadHtmlBody(comp, "", codepage);
        string? messageId = ReadStringProp(comp, "", 0x1035, codepage);
        if (messageId is { Length: 0 }) messageId = null;

        string? date = ReadFiletimeProp(comp, "", 0x0039) ?? ReadFiletimeProp(comp, "", 0x0E06);
        if (date is null)
        {
            string? headers = ReadStringProp(comp, "", 0x007D, codepage);
            if (headers is not null)
                foreach (var line in headers.Split('\n'))
                    if (line.StartsWith("Date:", StringComparison.Ordinal))
                    { date = line["Date:".Length..].Trim(); break; }
        }

        var (toEmails, ccEmails, bccEmails) = ReadRecipients(comp, codepage);

        string? rtfBody = null;
        if (MsgStream(comp, "/__substg1.0_10090102") is { } rtfComp)
        {
            byte[]? rtf = DecompressRtf(rtfComp);
            if (rtf is not null)
            {
                string stripped = StripRtfToPlainText(rtf);
                if (stripped.Length > 0) rtfBody = stripped;
            }
        }

        string? plainText = body is { Length: > 0 } ? body : null;
        string? htmlContent = htmlBody is { Length: > 0 } ? htmlBody : null;

        string contentStr = plainText ?? rtfBody ?? "";

        var attachments = new List<EmailAttachment>();
        foreach (var e in comp.Walk().Where(e => e.IsStorage && e.Name.StartsWith("__attach_", StringComparison.Ordinal)))
        {
            string path = e.Path;
            string? longName = ReadStringProp(comp, path, 0x3707, codepage);
            string? shortName = ReadStringProp(comp, path, 0x3704, codepage);
            string? displayName = ReadStringProp(comp, path, 0x3001, codepage);
            string? extension = ReadStringProp(comp, path, 0x3703, codepage);
            string? mimeTag = ReadStringProp(comp, path, 0x370E, codepage);

            string? filename = longName ?? shortName ?? displayName ?? (extension is not null ? $"attachment{extension}" : null);
            byte[]? binaryData = MsgStream(comp, $"{path}/__substg1.0_37010102");
            int? size = binaryData?.Length;
            string mimeType = mimeTag is { Length: > 0 } ? mimeTag : "application/octet-stream";

            attachments.Add(new EmailAttachment
            {
                Name = filename,
                Filename = filename,
                MimeType = mimeType,
                Size = size,
                IsImage = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
                Data = binaryData,
            });
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (subject is not null) metadata["subject"] = subject;
        if (fromEmail is not null) metadata["email_from"] = fromEmail;
        if (senderName is { Length: > 0 }) metadata["from_name"] = senderName;
        if (toEmails.Count > 0) metadata["email_to"] = string.Join(", ", toEmails);
        if (ccEmails.Count > 0) metadata["email_cc"] = string.Join(", ", ccEmails);
        if (bccEmails.Count > 0) metadata["email_bcc"] = string.Join(", ", bccEmails);
        if (date is not null) metadata["date"] = date;
        if (messageId is not null) metadata["message_id"] = messageId;

        return new EmailExtractionResult
        {
            Subject = subject,
            FromEmail = fromEmail,
            ToEmails = toEmails,
            CcEmails = ccEmails,
            BccEmails = bccEmails,
            Date = date,
            MessageId = messageId,
            PlainText = plainText,
            HtmlContent = htmlContent,
            Content = contentStr,
            Attachments = attachments,
            Metadata = metadata,
        };
    }

    private static uint? ReadIntProp(CompoundFile comp, string @base, ushort propId)
    {
        byte[]? buf = comp.TryReadStream($"{@base}/__properties_version1.0");
        if (buf is null) return null;
        int headerSize = @base.Length == 0 ? 32 : 8;
        for (int off = headerSize; off + 16 <= buf.Length; off += 16)
        {
            int ptype = OleUtil.U16(buf, off);
            int pid = OleUtil.U16(buf, off + 2);
            if (pid == propId && ptype == 0x0003)
                return OleUtil.U32(buf, off + 8);
        }
        return null;
    }

    /// <summary>Read a stream, treating empty streams as absent (mirrors Rust <c>read_msg_stream</c>).</summary>
    private static byte[]? MsgStream(CompoundFile comp, string path)
    {
        byte[]? b = comp.TryReadStream(path);
        return b is { Length: > 0 } ? b : null;
    }

    /// <summary>
    /// Read PR_HTML (0x1013). Outlook usually stores it as PT_BINARY (<c>…10130102</c>) rather
    /// than as a string property, so the string form is tried first and the raw stream is
    /// decoded as a fallback — otherwise an HTML-only message extracts its headers and no body.
    /// </summary>
    private static string? ReadHtmlBody(CompoundFile comp, string @base, uint? codepage)
    {
        if (ReadStringProp(comp, @base, 0x1013, codepage) is { Length: > 0 } s) return s;

        byte[]? buf = MsgStream(comp, $"{@base}/__substg1.0_10130102");
        if (buf is null) return null;

        if (codepage is not null)
            return EncodingForWindowsCodepage(codepage).GetString(buf).TrimEnd('\0');

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(buf).TrimStart('﻿').TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(buf).TrimEnd('\0');
        }
    }

    private static string? ReadStringProp(CompoundFile comp, string @base, ushort propId, uint? codepage)
    {
        byte[]? uni = MsgStream(comp, $"{@base}/__substg1.0_{propId:X4}001F");
        if (uni is not null) return DecodeUtf16Le(uni);

        byte[]? ansi = MsgStream(comp, $"{@base}/__substg1.0_{propId:X4}001E");
        if (ansi is null) return null;
        var enc = EncodingForWindowsCodepage(codepage);
        return enc.GetString(ansi).TrimEnd('\0');
    }

    private static string DecodeUtf16Le(byte[] data)
    {
        int count = data.Length / 2;
        var sb = new StringBuilder(count);
        for (int i = 0; i < count; i++)
            sb.Append((char)(ushort)(data[i * 2] | (data[i * 2 + 1] << 8)));
        return sb.ToString().TrimEnd('\0');
    }

    private static string? ReadFiletimeProp(CompoundFile comp, string @base, ushort propId)
    {
        byte[]? buf = comp.TryReadStream($"{@base}/__properties_version1.0");
        if (buf is null) return null;
        int headerSize = @base.Length == 0 ? 32 : 8;
        for (int off = headerSize; off + 16 <= buf.Length; off += 16)
        {
            int ptype = OleUtil.U16(buf, off);
            int pid = OleUtil.U16(buf, off + 2);
            if (pid == propId && ptype == 0x0040)
            {
                ulong filetime = OleUtil.U32(buf, off + 8) | ((ulong)OleUtil.U32(buf, off + 12) << 32);
                return FiletimeToIso8601(filetime);
            }
        }
        return null;
    }

    private static string? FiletimeToIso8601(ulong filetime)
    {
        const ulong epochDiff = 116_444_736_000_000_000UL;
        if (filetime < epochDiff) return null;
        ulong hundredNs = filetime - epochDiff;
        long secs = (long)(hundredNs / 10_000_000);
        uint nanos = (uint)((hundredNs % 10_000_000) * 100);

        long daysSinceEpoch = secs / 86400;
        long timeOfDay = secs % 86400;
        long hour = timeOfDay / 3600, min = (timeOfDay % 3600) / 60, sec = timeOfDay % 60;

        long z = daysSinceEpoch + 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long y = yoe + era * 400;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        long d = doy - (153 * mp + 2) / 5 + 1;
        long m = mp < 10 ? mp + 3 : mp - 9;
        if (m <= 2) y += 1;

        return nanos == 0
            ? $"{y:D4}-{m:D2}-{d:D2}T{hour:D2}:{min:D2}:{sec:D2}+00:00"
            : $"{y:D4}-{m:D2}-{d:D2}T{hour:D2}:{min:D2}:{sec:D2}.{nanos / 1_000_000:D3}+00:00";
    }

    private static (List<string> To, List<string> Cc, List<string> Bcc) ReadRecipients(CompoundFile comp, uint? codepage)
    {
        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();
        foreach (var e in comp.Walk().Where(e => e.IsStorage && e.Name.StartsWith("__recip_version1.0_", StringComparison.Ordinal)))
        {
            string path = e.Path;
            string? displayName = ReadStringProp(comp, path, 0x3001, codepage);
            string? emailAddr = ReadStringProp(comp, path, 0x39FE, codepage) ?? ReadStringProp(comp, path, 0x3003, codepage);
            if (emailAddr is { Length: 0 }) emailAddr = null;

            string formatted;
            if (displayName is { Length: > 0 } && emailAddr is not null && displayName != emailAddr)
                formatted = $"\"{displayName}\" <{emailAddr}>";
            else if (emailAddr is not null)
                formatted = emailAddr;
            else if (displayName is { Length: > 0 })
                formatted = displayName;
            else continue;

            uint recipType = ReadRecipType(comp, path);
            switch (recipType)
            {
                case 1: to.Add(formatted); break;
                case 2: cc.Add(formatted); break;
                case 3: bcc.Add(formatted); break;
                default: to.Add(formatted); break;
            }
        }
        return (to, cc, bcc);
    }

    private static uint ReadRecipType(CompoundFile comp, string @base)
    {
        byte[]? buf = comp.TryReadStream($"{@base}/__properties_version1.0");
        if (buf is null) return 0;
        for (int off = 8; off + 16 <= buf.Length; off += 16)
        {
            int ptype = OleUtil.U16(buf, off);
            int pid = OleUtil.U16(buf, off + 2);
            if (pid == 0x0C15 && ptype == 0x0003)
                return OleUtil.U32(buf, off + 8);
        }
        return 0;
    }

    private static Encoding EncodingForWindowsCodepage(uint? cp)
    {
        string label = cp switch
        {
            65001 => "utf-8",
            20127 => "us-ascii",
            1250 => "windows-1250", 1251 => "windows-1251", 1252 => "windows-1252",
            1253 => "windows-1253", 1254 => "windows-1254", 1255 => "windows-1255",
            1256 => "windows-1256", 1257 => "windows-1257", 1258 => "windows-1258",
            932 or 10001 => "shift_jis",
            936 or 10008 => "gbk",
            949 or 10003 => "euc-kr",
            950 or 10002 => "big5",
            28591 => "iso-8859-1", 28592 => "iso-8859-2", 28595 => "iso-8859-5",
            28597 => "iso-8859-7", 28599 => "iso-8859-9",
            _ => "windows-1252",
        };
        return CharsetDecoder.ResolveEncoding(label);
    }

    // ── compressed RTF (MS-OXRTFCP) ──────────────────────────────────────────────
    private const int MaxRtfCapacity = 16 * 1024 * 1024;
    private static readonly byte[] RtfPrebuf = Encoding.ASCII.GetBytes(
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}" +
        "{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New Roman" +
        "Courier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\ul\\ob\\strike" +
        "\\scaps\\outline\\shadow\\imprint\\emboss\\lang1024\\sbasedon1033\\fcharset0 {\\*\\cs10 \\additive " +
        "Default Paragraph Font}");

    private static byte[]? DecompressRtf(byte[] data)
    {
        if (data.Length < 16) return null;
        int compSize = (int)OleUtil.U32(data, 0);
        uint rawSize = OleUtil.U32(data, 4);
        uint magic = OleUtil.U32(data, 8);

        if (magic == 0x414c_454d) // "MELA" uncompressed
        {
            int len = Math.Max(0, compSize - 12);
            if (16 + len > data.Length) return null;
            var outp = new byte[len];
            Array.Copy(data, 16, outp, 0, len);
            return outp;
        }
        if (magic != 0x75465a4c) return null; // "LZFu"

        var dict = new byte[4096];
        int prebufLen = RtfPrebuf.Length;
        Array.Copy(RtfPrebuf, dict, prebufLen);
        int dictWrite = prebufLen;

        int inputStart = 16;
        int end = inputStart + Math.Min(Math.Max(0, compSize - 12), data.Length - inputStart);
        var output = new List<byte>(Math.Min((int)rawSize, MaxRtfCapacity));
        int pos = inputStart;

        while (pos < end)
        {
            byte control = data[pos++];
            for (int bit = 0; bit < 8; bit++)
            {
                if (pos >= end) return output.ToArray();
                if ((control & (1 << (7 - bit))) != 0)
                {
                    if (pos + 1 >= data.Length) return output.ToArray();
                    int hi = data[pos];
                    int lo = data[pos + 1];
                    pos += 2;
                    int offset = (hi << 4) | (lo >> 4);
                    int length = (lo & 0x0F) + 2;
                    for (int i = 0; i < length; i++)
                    {
                        byte bb = dict[(offset + i) & 0xFFF];
                        output.Add(bb);
                        dict[dictWrite & 0xFFF] = bb;
                        dictWrite++;
                    }
                }
                else
                {
                    if (pos >= data.Length) return output.ToArray();
                    byte bb = data[pos++];
                    output.Add(bb);
                    dict[dictWrite & 0xFFF] = bb;
                    dictWrite++;
                }
            }
        }
        return output.ToArray();
    }

    private static string StripRtfToPlainText(byte[] rtf)
    {
        string text = new UTF8Encoding(false, false).GetString(rtf);
        int len = text.Length;
        var output = new StringBuilder(len / 2);
        int i = 0;
        int? skipDepth = null;
        int depth = 0;

        while (i < len)
        {
            char ch = text[i];
            if (ch == '{')
            {
                depth++;
                i++;
                if (i + 1 < len && text[i] == '\\' && text[i + 1] == '*' && skipDepth is null)
                    skipDepth = depth;
                if (skipDepth is null)
                {
                    string rest = text.Substring(i);
                    if (rest.StartsWith("\\fonttbl", StringComparison.Ordinal) ||
                        rest.StartsWith("\\colortbl", StringComparison.Ordinal) ||
                        rest.StartsWith("\\stylesheet", StringComparison.Ordinal) ||
                        rest.StartsWith("\\info", StringComparison.Ordinal))
                        skipDepth = depth;
                }
            }
            else if (ch == '}')
            {
                if (skipDepth is { } sd && depth <= sd) skipDepth = null;
                depth = Math.Max(0, depth - 1);
                i++;
            }
            else if (ch == '\\' && skipDepth is null)
            {
                i++;
                if (i >= len) break;
                char n = text[i];
                if (n == '\\') { output.Append('\\'); i++; }
                else if (n == '{') { output.Append('{'); i++; }
                else if (n == '}') { output.Append('}'); i++; }
                else if (n == '\'')
                {
                    i++;
                    if (i + 2 <= len)
                    {
                        string hex = text.Substring(i, 2);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int bv))
                            output.Append(OleUtil.Cp1252ToChar((byte)bv));
                        i += 2;
                    }
                }
                else if (n == 'u' && i + 1 < len && (char.IsAsciiDigit(text[i + 1]) || text[i + 1] == '-'))
                {
                    i++;
                    int start = i;
                    if (i < len && text[i] == '-') i++;
                    while (i < len && char.IsAsciiDigit(text[i])) i++;
                    if (int.TryParse(text.Substring(start, i - start), out int code))
                    {
                        uint cp = code < 0 ? (uint)(code + 65536) : (uint)code;
                        if (cp <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF)) output.Append(char.ConvertFromUtf32((int)cp));
                    }
                    if (i < len && text[i] == ' ') i++;
                    if (i < len && text[i] != '\\' && text[i] != '{' && text[i] != '}') i++;
                }
                else
                {
                    int wordStart = i;
                    while (i < len && char.IsAsciiLetter(text[i])) i++;
                    string word = text.Substring(wordStart, i - wordStart);
                    if (i < len && (text[i] == '-' || char.IsAsciiDigit(text[i])))
                    {
                        if (text[i] == '-') i++;
                        while (i < len && char.IsAsciiDigit(text[i])) i++;
                    }
                    if (i < len && text[i] == ' ') i++;
                    if (word is "par" or "line") output.Append('\n');
                    else if (word == "tab") output.Append('\t');
                }
            }
            else if ((ch == '\r' || ch == '\n') && skipDepth is null) i++;
            else if (skipDepth is not null) i++;
            else { output.Append(ch); i++; }
        }

        var result = new StringBuilder(output.Length);
        int prevNl = 0;
        foreach (char c in output.ToString())
        {
            if (c == '\n') { prevNl++; if (prevNl <= 2) result.Append('\n'); }
            else { prevNl = 0; result.Append(c); }
        }
        return result.ToString().Trim();
    }
}
