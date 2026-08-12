using System.Text;
using Xberg.Core;
using Xberg.Types;

namespace Xberg.Extractors;

/// <summary>
/// JATS (Journal Article Tag Suite) extractor. Ported from Rust `extractors/jats/`.
/// Two passes over the shared lenient XML pull reader: a metadata pass (title, authors,
/// affiliations, DOI/PII, keywords, dates, journal info, abstract) and a content pass
/// building headings/paragraphs/tables/references into an InternalDocument.
///
/// Gap: the C# <see cref="JatsMetadata"/> payload is an empty stub, so the typed
/// format-metadata fields (copyright/license/contributor_roles) cannot be emitted;
/// only <c>format_type: "jats"</c> is produced.
/// </summary>
public sealed class JatsExtractor : IExtractor
{
    public IEnumerable<string> SupportedMimeTypes => new[]
    {
        "application/x-jats+xml",
        "text/jats",
    };

    public int Priority => 50;

    public InternalDocument Extract(ReadOnlySpan<byte> content, string mimeType, ExtractionConfig config)
    {
        string jats = XmlPullReader.Decode(content);
        var jm = ExtractAllMetadata(jats);

        var metadata = new Metadata();
        var subjectParts = new List<string>();

        if (jm.Title.Length > 0) { metadata.Title = jm.Title; metadata.Subject = jm.Title; subjectParts.Add($"Title: {jm.Title}"); }
        if (jm.Subtitle is not null) subjectParts.Add($"Subtitle: {jm.Subtitle}");
        if (jm.Authors.Count > 0) { metadata.Authors = new List<string>(jm.Authors); subjectParts.Add($"Authors: {string.Join("; ", jm.Authors)}"); }
        if (jm.Affiliations.Count > 0) subjectParts.Add($"Affiliations: {string.Join("; ", jm.Affiliations)}");
        if (jm.Doi is not null) subjectParts.Add($"DOI: {jm.Doi}");
        if (jm.Pii is not null) subjectParts.Add($"PII: {jm.Pii}");
        if (jm.Keywords.Count > 0) { metadata.Keywords = new List<string>(jm.Keywords); subjectParts.Add($"Keywords: {string.Join("; ", jm.Keywords)}"); }
        if (jm.PublicationDate is not null) { metadata.CreatedAt = jm.PublicationDate; subjectParts.Add($"Publication Date: {jm.PublicationDate}"); }
        if (jm.Volume is not null) subjectParts.Add($"Volume: {jm.Volume}");
        if (jm.Issue is not null) subjectParts.Add($"Issue: {jm.Issue}");
        if (jm.Pages is not null) subjectParts.Add($"Pages: {jm.Pages}");
        if (jm.JournalTitle is not null) subjectParts.Add($"Journal: {jm.JournalTitle}");
        if (jm.ArticleType is not null) subjectParts.Add($"Article Type: {jm.ArticleType}");
        if (jm.AbstractText is not null) subjectParts.Add($"Abstract: {jm.AbstractText}");
        if (jm.CorrespondingAuthor is not null) subjectParts.Add($"Corresponding Author: {jm.CorrespondingAuthor}");
        foreach (var (dtype, dval) in jm.HistoryDates)
            subjectParts.Add($"{Capitalize(dtype)}: {dval}");
        if (jm.CopyrightStatement is not null) subjectParts.Add($"Copyright: {jm.CopyrightStatement}");

        var jatsPayload = new JatsMetadata
        {
            Copyright = jm.CopyrightStatement,
            License = jm.License,
        };
        foreach (var (dtype, dval) in jm.HistoryDates) jatsPayload.HistoryDates[dtype] = dval;
        foreach (var (name, role) in jm.ContributorRoles)
            jatsPayload.ContributorRoles.Add(new ContributorRole { Name = name, Role = role.Length == 0 ? null : role });
        metadata.Format = new FormatMetadata { FormatType = "jats", Payload = jatsPayload };

        if (subjectParts.Count > 0) metadata.Subject = string.Join(" | ", subjectParts);

        var doc = BuildInternalDocument(jats);
        doc.MimeType = mimeType;
        doc.Metadata = metadata;

        if (jm.Doi is not null)
            doc.PushUri(new ExtractedUri { Url = $"https://doi.org/{jm.Doi}", Label = $"DOI: {jm.Doi}", Kind = UriKind.Citation });

        return doc;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── content pass (mirrors build_jats_internal_document) ──────────────────
    private static InternalDocument BuildInternalDocument(string content)
    {
        var reader = new XmlPullReader(content);
        var builder = new InternalDocumentBuilder("jats");

        bool inArticleMeta = false, inAbstract = false, inBody = false, inBack = false, inRefList = false;
        bool inTable = false, inThead = false, inTbody = false, inRow = false;
        var currentTable = new List<List<string>>();
        var currentRow = new List<string>();
        uint secDepth = 0;
        bool refListOpened = false;

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                string tag = ev.Name;
                switch (tag)
                {
                    case "article-meta": inArticleMeta = true; break;
                    case "article-title" when inArticleMeta && !inAbstract:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) builder.PushHeading(1, t, null, null);
                        break;
                    }
                    case "abstract" when inArticleMeta:
                        inAbstract = true; builder.PushHeading(2, "Abstract", null, null); break;
                    case "title" when inAbstract:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) builder.PushHeading(3, t, null, null);
                        break;
                    }
                    case "p" when inAbstract:
                    {
                        var (t, anns) = ExtractParaWithAnnotations(reader);
                        if (t.Length > 0) builder.PushParagraph(t, anns, null, null);
                        break;
                    }
                    case "body": inBody = true; secDepth = 0; break;
                    case "sec" when inBody: secDepth++; break;
                    case "title" when inBody && !inArticleMeta && !inRefList:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) { byte level = (byte)Math.Min(secDepth + 1, 6); builder.PushHeading(level, t, null, null); }
                        break;
                    }
                    case "p" when inBody:
                    {
                        var (t, anns) = ExtractParaWithAnnotations(reader);
                        if (t.Length > 0)
                        {
                            foreach (var ann in anns)
                                if (ann.Kind.Which == AnnotationKind.Tag.Link && !string.IsNullOrEmpty(ann.Kind.Url))
                                    builder.PushUri(new ExtractedUri { Url = ann.Kind.Url!, Label = SliceLabel(t, ann.Start, ann.End), Kind = UriKind.Hyperlink });
                            builder.PushParagraph(t, anns, null, null);
                        }
                        break;
                    }
                    case "fig" when inBody: ExtractText(reader); break;
                    case "disp-formula" when inBody:
                    case "inline-formula" when inBody:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) builder.PushFormula(t, null, null);
                        break;
                    }
                    case "back": inBack = true; break;
                    case "supplementary-material" when inBack: ExtractText(reader); break;
                    case "title" when inBack && !inRefList:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) builder.PushHeading(2, t, null, null);
                        break;
                    }
                    case "p" when inBack && !inRefList:
                    {
                        var (t, anns) = ExtractParaWithAnnotations(reader);
                        if (t.Length > 0) builder.PushParagraph(t, anns, null, null);
                        break;
                    }
                    case "table": inTable = true; currentTable.Clear(); break;
                    case "thead" when inTable: inThead = true; break;
                    case "tbody" when inTable: inTbody = true; break;
                    case "tr" when (inThead || inTbody) && inTable: inRow = true; currentRow.Clear(); break;
                    case "td" when inRow:
                    case "th" when inRow:
                        currentRow.Add(ExtractText(reader)); break;
                    case "ref-list": inRefList = true; break;
                    case "title" when inRefList:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) builder.PushHeading(2, t, null, null);
                        break;
                    }
                    case "ref" when inRefList:
                    {
                        string t = ExtractCitationText(reader);
                        if (t.Length > 0)
                        {
                            if (!refListOpened) { builder.PushList(true); refListOpened = true; }
                            builder.PushListItem(t, true, new(), null, null);
                        }
                        break;
                    }
                }
            }
            else if (ev.Kind == XmlEv.End)
            {
                string tag = ev.Name;
                switch (tag)
                {
                    case "article-meta": inArticleMeta = false; break;
                    case "abstract": if (inAbstract) inAbstract = false; break;
                    case "body": inBody = false; break;
                    case "sec": if (inBody && secDepth > 0) secDepth--; break;
                    case "back": inBack = false; break;
                    case "ref-list":
                        if (refListOpened) { builder.EndList(); refListOpened = false; }
                        inRefList = false; break;
                    case "table":
                        if (inTable) { if (currentTable.Count > 0) { builder.PushTableFromCells(currentTable, null, null); currentTable.Clear(); } inTable = false; } break;
                    case "thead": if (inThead) inThead = false; break;
                    case "tbody": if (inTbody) inTbody = false; break;
                    case "tr": if (inRow) { if (currentRow.Count > 0) { currentTable.Add(new List<string>(currentRow)); currentRow.Clear(); } inRow = false; } break;
                }
            }
        }
        return builder.Build();
    }

    // ── extract_text_content (parser.rs) ─────────────────────────────────────
    internal static string ExtractText(XmlPullReader reader)
    {
        var text = new StringBuilder();
        int depth = 0;
        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start) depth++;
            else if (ev.Kind == XmlEv.End)
            {
                if (depth == 0) break;
                depth--;
                if (text.Length > 0 && text[^1] != '\n') text.Append(' ');
            }
            else if (ev.Kind == XmlEv.Text)
            {
                if (ev.Text.Trim().Length > 0) { text.Append(ev.Text); text.Append(' '); }
            }
            else if (ev.Kind == XmlEv.CData)
            {
                if (ev.Text.Trim().Length > 0) { text.Append(ev.Text); text.Append('\n'); }
            }
        }
        return text.ToString().Trim();
    }

    // ── extract_citation_text (parser.rs) ────────────────────────────────────
    private static string ExtractCitationText(XmlPullReader reader)
    {
        int depth = 0;
        bool inElementCitation = false, inMixedCitation = false, inPersonGroup = false, inName = false;
        var authors = new List<string>();
        var surname = new StringBuilder();
        var given = new StringBuilder();
        var articleTitle = new StringBuilder();
        var source = new StringBuilder();
        var year = new StringBuilder();
        var volume = new StringBuilder();
        var fpage = new StringBuilder();
        var lpage = new StringBuilder();
        string currentTag = "";
        var mixed = new StringBuilder();

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                depth++;
                string tag = ev.Name;
                switch (tag)
                {
                    case "element-citation": inElementCitation = true; break;
                    case "mixed-citation": inMixedCitation = true; break;
                    case "person-group" when inElementCitation: inPersonGroup = true; break;
                    case "name" when inPersonGroup: inName = true; surname.Clear(); given.Clear(); break;
                    case "surname": case "given-names": case "article-title": case "source":
                    case "year": case "volume": case "fpage": case "lpage":
                        if (inElementCitation) currentTag = tag; break;
                }
            }
            else if (ev.Kind == XmlEv.End)
            {
                if (depth == 0) break;
                string tag = ev.Name;
                switch (tag)
                {
                    case "name" when inName:
                    {
                        inName = false;
                        var a = new StringBuilder();
                        if (surname.Length > 0) a.Append(surname.ToString().Trim());
                        if (given.Length > 0) { if (a.Length > 0) a.Append(' '); a.Append(given.ToString().Trim()); }
                        if (a.Length > 0) authors.Add(a.ToString());
                        break;
                    }
                    case "person-group": inPersonGroup = false; break;
                    case "element-citation": inElementCitation = false; break;
                    case "mixed-citation": inMixedCitation = false; break;
                }
                currentTag = "";
                depth--;
            }
            else if (ev.Kind == XmlEv.Text)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0)
                {
                    if (inMixedCitation) { if (mixed.Length > 0) mixed.Append(' '); mixed.Append(trimmed); }
                    else if (inElementCitation)
                    {
                        switch (currentTag)
                        {
                            case "surname": surname.Append(trimmed); break;
                            case "given-names": given.Append(trimmed); break;
                            case "article-title": if (articleTitle.Length > 0) articleTitle.Append(' '); articleTitle.Append(trimmed); break;
                            case "source": source.Append(trimmed); break;
                            case "year": year.Append(trimmed); break;
                            case "volume": volume.Append(trimmed); break;
                            case "fpage": fpage.Append(trimmed); break;
                            case "lpage": lpage.Append(trimmed); break;
                        }
                    }
                }
            }
        }

        if (mixed.Length > 0) return mixed.ToString();

        var citation = new StringBuilder();
        if (authors.Count > 0) { citation.Append(string.Join(", ", authors)); citation.Append(". "); }
        if (articleTitle.Length > 0) { citation.Append(articleTitle); citation.Append(". "); }
        if (source.Length > 0) { citation.Append(source); citation.Append('.'); }
        if (year.Length > 0) { citation.Append(' '); citation.Append(year); }
        if (volume.Length > 0) { citation.Append(';'); citation.Append(volume); }
        if (fpage.Length > 0) { citation.Append(':'); citation.Append(fpage); if (lpage.Length > 0) { citation.Append('-'); citation.Append(lpage); } }
        if (citation.Length > 0 && citation[^1] != '.') citation.Append('.');
        return citation.ToString().Trim();
    }

    // ── extract_para_with_annotations_jats (mod.rs) ──────────────────────────
    private static (string text, List<TextAnnotation> anns) ExtractParaWithAnnotations(XmlPullReader reader)
    {
        var text = new StringBuilder();
        var anns = new List<TextAnnotation>();
        int depth = 0;
        var stack = new List<(string Kind, int OpenDepth, int Start, string? Href)>();

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                depth++;
                switch (ev.Name)
                {
                    case "italic": stack.Add(("italic", depth, Utf8Len(text), null)); break;
                    case "bold": stack.Add(("bold", depth, Utf8Len(text), null)); break;
                    case "underline": stack.Add(("underline", depth, Utf8Len(text), null)); break;
                    case "sub": stack.Add(("subscript", depth, Utf8Len(text), null)); break;
                    case "sup": stack.Add(("superscript", depth, Utf8Len(text), null)); break;
                    case "ext-link":
                    {
                        string? href = null;
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs)
                                if (k == "xlink:href" || k.EndsWith(":href", StringComparison.Ordinal) || k == "href") href = v;
                        stack.Add(("link", depth, Utf8Len(text), href)); break;
                    }
                }
            }
            else if (ev.Kind == XmlEv.End)
            {
                if (depth == 0) break;
                if (stack.Count > 0)
                {
                    var top = stack[^1];
                    if (top.OpenDepth == depth)
                    {
                        int end = Utf8Len(text);
                        int actualStart = top.Start;
                        string span = Utf8Substring(text.ToString(), top.Start, end);
                        if (span.Length != span.TrimStart().Length)
                            actualStart = end - Encoding.UTF8.GetByteCount(span.TrimStart());
                        if (end > actualStart) anns.Add(MakeAnnotation(top.Kind, (uint)actualStart, (uint)end, top.Href));
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                depth--;
            }
            else if (ev.Kind == XmlEv.Text)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0)
                {
                    if (text.Length > 0 && text[^1] != ' ' && text[^1] != '\n') text.Append(' ');
                    text.Append(trimmed);
                }
            }
            else if (ev.Kind == XmlEv.CData)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0) { if (text.Length > 0) text.Append(' '); text.Append(trimmed); }
            }
        }
        return (text.ToString().Trim(), anns);
    }

    private static TextAnnotation MakeAnnotation(string kind, uint start, uint end, string? href) => kind switch
    {
        "bold" => new TextAnnotation { Start = start, End = end, Kind = AnnotationKind.Bold },
        "italic" => new TextAnnotation { Start = start, End = end, Kind = AnnotationKind.Italic },
        "underline" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Underline } },
        "subscript" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Subscript } },
        "superscript" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Superscript } },
        "link" => new TextAnnotation { Start = start, End = end, Kind = new AnnotationKind { Which = AnnotationKind.Tag.Link, Url = href ?? "" } },
        _ => new TextAnnotation { Start = start, End = end, Kind = AnnotationKind.Bold },
    };

    private static int Utf8Len(StringBuilder sb) => Encoding.UTF8.GetByteCount(sb.ToString());
    private static string Utf8Substring(string s, int startByte, int endByte)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (startByte < 0 || endByte > bytes.Length || startByte > endByte) return "";
        return Encoding.UTF8.GetString(bytes, startByte, endByte - startByte);
    }
    private static string? SliceLabel(string text, uint startByte, uint endByte)
    {
        var span = Utf8Substring(text, (int)startByte, (int)endByte);
        return span.Length == 0 ? null : span;
    }

    // ── metadata pass (mirrors extract_jats_all_in_one, metadata fields only) ─
    private sealed class JatsMeta
    {
        public string Title = "";
        public string? Subtitle;
        public readonly List<string> Authors = new();
        public readonly List<string> Affiliations = new();
        public string? Doi, Pii;
        public readonly List<string> Keywords = new();
        public string? PublicationDate, Volume, Issue, Pages, JournalTitle, ArticleType, AbstractText, CorrespondingAuthor;
        public readonly List<(string, string)> HistoryDates = new();
        public string? CopyrightStatement, License;
        public readonly List<(string, string)> ContributorRoles = new();
    }

    private static JatsMeta ExtractAllMetadata(string content)
    {
        var reader = new XmlPullReader(content);
        var m = new JatsMeta();

        bool inArticleMeta = false, inArticleTitle = false, inSubtitle = false, inContrib = false;
        bool inName = false, inAff = false, inAbstract = false, inKwdGroup = false, inKwd = false;
        bool inHistory = false, inPermissions = false;
        var currentAuthor = new StringBuilder();
        var currentAff = new StringBuilder();
        var abstractContent = new StringBuilder();
        string currentContribType = "";

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;
            if (ev.Kind == XmlEv.Start)
            {
                string tag = ev.Name;
                switch (tag)
                {
                    case "article":
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs) if (k == "article-type") m.ArticleType = v;
                        break;
                    case "article-meta": inArticleMeta = true; break;
                    case "article-title" when inArticleMeta: inArticleTitle = true; break;
                    case "subtitle" when inArticleMeta: inSubtitle = true; break;
                    case "contrib" when inArticleMeta:
                        inContrib = true; currentAuthor.Clear(); currentContribType = "";
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs) if (k == "contrib-type") currentContribType = v;
                        break;
                    case "name" when inContrib: inName = true; break;
                    case "aff" when inArticleMeta: inAff = true; currentAff.Clear(); break;
                    case "article-id" when inArticleMeta:
                    {
                        string idType = "";
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs) if (k == "pub-id-type") idType = v;
                        string idText = ExtractText(reader);
                        if (idType == "doi") m.Doi = idText;
                        else if (idType == "pii") m.Pii = idText;
                        break;
                    }
                    case "volume" when inArticleMeta: m.Volume = ExtractText(reader); break;
                    case "issue" when inArticleMeta: m.Issue = ExtractText(reader); break;
                    case "fpage" when inArticleMeta:
                    case "lpage" when inArticleMeta:
                    {
                        string pageText = ExtractText(reader);
                        m.Pages = m.Pages is null ? pageText : m.Pages + "-" + pageText;
                        break;
                    }
                    case "pub-date" when inArticleMeta:
                    {
                        string dt = ExtractText(reader);
                        if (m.PublicationDate is null) m.PublicationDate = dt;
                        break;
                    }
                    case "journal-title" when inArticleMeta:
                    {
                        string jt = ExtractText(reader);
                        if (m.JournalTitle is null) m.JournalTitle = jt;
                        break;
                    }
                    case "abstract" when inArticleMeta: inAbstract = true; abstractContent.Clear(); break;
                    case "kwd-group" when inArticleMeta: inKwdGroup = true; break;
                    case "kwd" when inKwdGroup: inKwd = true; break;
                    case "corresp" when inArticleMeta: m.CorrespondingAuthor = ExtractText(reader); break;
                    case "history" when inArticleMeta: inHistory = true; break;
                    case "date" when inHistory:
                    {
                        string dateType = "";
                        if (ev.Attrs is not null)
                            foreach (var (k, v) in ev.Attrs) if (k == "date-type") dateType = v;
                        string dateText = ExtractText(reader);
                        if (dateText.Length > 0 && dateType.Length > 0) m.HistoryDates.Add((dateType, dateText));
                        break;
                    }
                    case "permissions" when inArticleMeta: inPermissions = true; break;
                    case "copyright-statement" when inPermissions:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) m.CopyrightStatement = t;
                        break;
                    }
                    case "license" when inPermissions:
                    {
                        string t = ExtractText(reader);
                        if (t.Length > 0) m.License = t;
                        break;
                    }
                }
            }
            else if (ev.Kind == XmlEv.End)
            {
                string tag = ev.Name;
                switch (tag)
                {
                    case "article-meta": inArticleMeta = false; break;
                    case "article-title": if (inArticleTitle) inArticleTitle = false; break;
                    case "subtitle": if (inSubtitle) inSubtitle = false; break;
                    case "contrib" when inContrib:
                        if (currentAuthor.Length > 0)
                        {
                            m.Authors.Add(currentAuthor.ToString());
                            if (currentContribType.Length > 0)
                                m.ContributorRoles.Add((currentAuthor.ToString(), currentContribType));
                        }
                        inContrib = false; currentAuthor.Clear(); currentContribType = ""; break;
                    case "name": if (inName) inName = false; break;
                    case "aff" when inAff:
                        if (currentAff.Length > 0) m.Affiliations.Add(currentAff.ToString());
                        inAff = false; currentAff.Clear(); break;
                    case "abstract" when inAbstract:
                        inAbstract = false; m.AbstractText = abstractContent.ToString().Trim(); break;
                    case "history": if (inHistory) inHistory = false; break;
                    case "permissions": if (inPermissions) inPermissions = false; break;
                    case "kwd-group": if (inKwdGroup) inKwdGroup = false; break;
                    case "kwd": if (inKwd) inKwd = false; break;
                }
            }
            else if (ev.Kind == XmlEv.Text)
            {
                string trimmed = ev.Text.Trim();
                if (trimmed.Length > 0)
                {
                    if (inArticleTitle && m.Title.Length == 0) m.Title += trimmed;
                    else if (inSubtitle && m.Subtitle is null) m.Subtitle = trimmed;
                    else if (inName) { if (currentAuthor.Length > 0) currentAuthor.Append(' '); currentAuthor.Append(trimmed); }
                    else if (inAff) { if (currentAff.Length > 0) currentAff.Append(' '); currentAff.Append(trimmed); }
                    else if (inAbstract) { if (abstractContent.Length > 0) abstractContent.Append(' '); abstractContent.Append(trimmed); }
                    else if (inKwd) m.Keywords.Add(trimmed);
                }
            }
        }
        return m;
    }
}
