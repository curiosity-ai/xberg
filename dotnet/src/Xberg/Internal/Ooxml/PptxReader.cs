using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Xberg.Internal.Ooxml;

/// <summary>Result of reading a PPTX: the assembled markdown/plain content plus metadata.</summary>
public sealed class PptxResult
{
    public string Content = "";
    public int SlideCount;   // actual number of slide parts
    public int ImageCount;   // Σ Image elements
    public int TableCount;   // Σ Table elements
    public uint AppSlideCount;             // docProps/app.xml <Slides>
    public List<string> SlideNames = new(); // app.xml TitlesOfParts
    public Dictionary<string, string> OfficeMetadata = new();
}

/// <summary>
/// Pure-managed PPTX reader — ports <c>extraction/pptx</c>. Builds the same markdown-ish
/// (or plain) <c>content</c> string the Rust pipeline feeds into the InternalDocument block
/// parser, plus office metadata.
/// </summary>
public static class PptxReader
{
    public static PptxResult Extract(ReadOnlySpan<byte> content, bool plain, bool injectPlaceholders)
    {
        using var pkg = new OoxmlPackage(content);
        var result = new PptxResult();

        var slidePaths = FindSlidePaths(pkg);
        result.SlideCount = slidePaths.Count;

        var notes = ExtractAllNotes(pkg, slidePaths);

        var main = new ContentBuilder(plain);
        for (int i = 0; i < slidePaths.Count; i++)
        {
            var xml = pkg.ReadXml(slidePaths[i]);
            var elements = xml is null ? new List<SlideElement>() : ParseSlide(xml);
            ResolveImageTargets(pkg, slidePaths[i], elements);
            result.ImageCount += elements.Count(e => e.Kind == SlideKind.Image);
            result.TableCount += elements.Count(e => e.Kind == SlideKind.Table);

            string slideContent = SlideToMarkdown(elements, plain, injectPlaceholders);
            main.AddText(slideContent);

            if (notes.TryGetValue((uint)(i + 1), out var noteText))
                main.AddNotes(noteText);
        }
        result.Content = main.Build();

        // Metadata
        var core = OfficeMetadata.ExtractCore(pkg);
        var app = OfficeMetadata.ExtractApp(pkg);
        BuildOfficeMetadata(result, core, app, pkg);
        return result;
    }

    // ── slide discovery + ordering ─────────────────────────────────────────────
    private static List<string> FindSlidePaths(OoxmlPackage pkg)
    {
        var paths = new List<string>();
        var rels = pkg.ReadXml("ppt/_rels/presentation.xml.rels");
        if (rels?.Root is not null)
        {
            foreach (var r in rels.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                var type = r.Attribute("Type")?.Value ?? "";
                if (!type.Contains("slide") || type.Contains("slideMaster")) continue;
                var target = r.Attribute("Target")?.Value;
                if (target is null) continue;
                if (target.StartsWith('/')) target = target[1..];
                if (!target.StartsWith("ppt/", StringComparison.Ordinal)) target = "ppt/" + target;
                paths.Add(target);
            }
        }
        if (paths.Count == 0)
        {
            foreach (var name in pkg.PartNames)
                if (name.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal))
                    paths.Add(name);
        }
        paths.Sort((a, b) => SlideNum(a).CompareTo(SlideNum(b)));
        return paths;
    }

    private static uint SlideNum(string path)
    {
        var file = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        if (!file.StartsWith("slide", StringComparison.Ordinal)) return uint.MaxValue;
        var mid = file["slide".Length..];
        if (mid.EndsWith(".xml", StringComparison.Ordinal)) mid = mid[..^4];
        return uint.TryParse(mid, out var n) ? n : uint.MaxValue;
    }

    // ── slide parsing ──────────────────────────────────────────────────────────
    private static List<SlideElement> ParseSlide(XDocument doc)
    {
        var elements = new List<SlideElement>();
        var root = doc.Root;
        if (root is null) return elements;
        var cSld = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "cSld");
        var spTree = cSld?.Elements().FirstOrDefault(e => e.Name.LocalName == "spTree");
        if (spTree is null) return elements;
        foreach (var child in spTree.Elements())
            ParseGroup(child, elements);
        return elements;
    }

    private static void ParseGroup(XElement node, List<SlideElement> elements)
    {
        var pos = ExtractPosition(node);
        switch (node.Name.LocalName)
        {
            case "sp":
                var parsed = ParseSp(node);
                if (parsed is not null) { parsed.Pos = pos; elements.Add(parsed); }
                break;
            case "graphicFrame":
                var tbl = ParseGraphicFrame(node);
                if (tbl is not null) { tbl.Pos = pos; elements.Add(tbl); }
                break;
            case "pic":
                var img = ParsePic(node);
                if (img is not null) { img.Pos = pos; elements.Add(img); }
                break;
            case "grpSp":
                foreach (var c in node.Elements()) ParseGroup(c, elements);
                break;
            default:
                elements.Add(new SlideElement { Kind = SlideKind.Unknown });
                break;
        }
    }

    private static bool IsTitlePlaceholder(XElement sp)
    {
        var nvSpPr = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "nvSpPr");
        var nvPr = nvSpPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "nvPr");
        var ph = nvPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "ph");
        var type = ph?.Attribute("type")?.Value;
        return type is "title" or "ctrTitle";
    }

    private static SlideElement? ParseSp(XElement sp)
    {
        var txBody = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
        if (txBody is null) return null;
        bool isTitle = IsTitlePlaceholder(sp);
        bool isList = !isTitle && txBody.Descendants().Any(n =>
            n.Name.LocalName == "pPr" &&
            (n.Attribute("lvl") is not null ||
             n.Elements().Any(c => c.Name.LocalName is "buAutoNum" or "buChar")));

        if (isList)
        {
            var el = new SlideElement { Kind = SlideKind.List };
            foreach (var p in txBody.Elements().Where(e => e.Name.LocalName == "p"))
            {
                var (level, ordered, hasBullet) = ParseListProperties(p);
                el.Items.Add(new ListItem { Level = level, IsOrdered = ordered, HasBullet = hasBullet, Runs = ParseParagraph(p, true) });
            }
            return el;
        }
        else
        {
            var el = new SlideElement { Kind = SlideKind.Text, IsTitle = isTitle };
            foreach (var p in txBody.Elements().Where(e => e.Name.LocalName == "p"))
                el.Runs.AddRange(ParseParagraph(p, true));
            return el;
        }
    }

    private static (uint Level, bool Ordered, bool HasBullet) ParseListProperties(XElement p)
    {
        uint level = 1; bool ordered = false, hasBullet = false;
        var pPr = p.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
        if (pPr is not null)
        {
            var lvl = pPr.Attribute("lvl")?.Value;
            if (lvl is not null) level = (uint.TryParse(lvl, out var v) ? v : 0) + 1;
            ordered = pPr.Elements().Any(e => e.Name.LocalName == "buAutoNum");
            bool buNone = pPr.Elements().Any(e => e.Name.LocalName == "buNone");
            hasBullet = !buNone && (ordered
                || pPr.Elements().Any(e => e.Name.LocalName == "buChar")
                || pPr.Attribute("lvl") is not null);
        }
        return (level, ordered, hasBullet);
    }

    private static SlideElement? ParseGraphicFrame(XElement node)
    {
        var gd = node.Descendants().FirstOrDefault(n =>
            n.Name.LocalName == "graphicData" &&
            n.Attribute("uri")?.Value == "http://schemas.openxmlformats.org/drawingml/2006/table");
        var tbl = gd?.Elements().FirstOrDefault(n => n.Name.LocalName == "tbl");
        if (tbl is null) return null;

        var el = new SlideElement { Kind = SlideKind.Table };
        foreach (var tr in tbl.Elements().Where(e => e.Name.LocalName == "tr"))
        {
            var row = new List<List<Run>>();
            foreach (var tc in tr.Elements().Where(e => e.Name.LocalName == "tc"))
            {
                var runs = new List<Run>();
                var body = tc.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
                if (body is not null)
                    foreach (var p in body.Elements().Where(e => e.Name.LocalName == "p"))
                        runs.AddRange(ParseParagraph(p, false));
                row.Add(runs);
            }
            el.TableRows.Add(row);
        }
        return el;
    }

    private static SlideElement? ParsePic(XElement pic)
    {
        var blip = pic.Descendants().FirstOrDefault(n => n.Name.LocalName == "blip");
        if (blip is null) return null;
        var embed = blip.Attributes().FirstOrDefault(a => a.Name.LocalName == "embed")?.Value;
        if (embed is null) return null;
        var cNvPr = pic.Descendants().FirstOrDefault(n => n.Name.LocalName == "cNvPr");
        var descr = cNvPr?.Attribute("descr")?.Value;
        if (string.IsNullOrEmpty(descr)) descr = null;
        return new SlideElement { Kind = SlideKind.Image, ImageId = embed, Description = descr };
    }

    private static List<Run> ParseParagraph(XElement p, bool addNewLine)
    {
        var runNodes = p.Elements().Where(e => e.Name.LocalName == "r").ToList();
        var runs = new List<Run>(runNodes.Count);
        for (int i = 0; i < runNodes.Count; i++)
        {
            var run = ParseRun(runNodes[i]);
            if (addNewLine && i == runNodes.Count - 1) run.Text += "\n";
            runs.Add(run);
        }
        return runs;
    }

    private static Run ParseRun(XElement r)
    {
        var run = new Run();
        var rPr = r.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
        if (rPr is not null)
        {
            var b = rPr.Attribute("b")?.Value;
            if (b is not null) run.Bold = b == "1" || b.Equals("true", StringComparison.OrdinalIgnoreCase);
            var it = rPr.Attribute("i")?.Value;
            if (it is not null) run.Italic = it == "1" || it.Equals("true", StringComparison.OrdinalIgnoreCase);
            var u = rPr.Attribute("u")?.Value;
            if (u is not null) run.Underline = u != "none";
            var strike = rPr.Attribute("strike")?.Value;
            if (strike is not null) run.Strike = strike is "sngStrike" or "dblStrike";
        }
        var t = r.Elements().FirstOrDefault(e => e.Name.LocalName == "t");
        if (t is not null) run.Text += t.Value;
        return run;
    }

    private static Position ExtractPosition(XElement node)
    {
        var xfrm = node.Descendants().FirstOrDefault(n => n.Name.LocalName == "xfrm");
        if (xfrm is null) return default;
        var off = xfrm.Elements().FirstOrDefault(n => n.Name.LocalName == "off");
        if (off is null) return default;
        if (!long.TryParse(off.Attribute("x")?.Value, out var x)) return default;
        if (!long.TryParse(off.Attribute("y")?.Value, out var y)) return default;
        long cx = 0, cy = 0;
        var ext = xfrm.Elements().FirstOrDefault(n => n.Name.LocalName == "ext");
        if (ext is not null)
        {
            long.TryParse(ext.Attribute("cx")?.Value, out cx);
            long.TryParse(ext.Attribute("cy")?.Value, out cy);
        }
        return new Position { X = x, Y = y, Cx = cx, Cy = cy };
    }

    // ── slide → content ────────────────────────────────────────────────────────
    private static string SlideToMarkdown(List<SlideElement> elements, bool plain, bool injectPlaceholders)
    {
        var builder = new ContentBuilder(plain);
        var order = Enumerable.Range(0, elements.Count).ToList();
        order.Sort((a, b) =>
        {
            var pa = elements[a].Pos; var pb = elements[b].Pos;
            int c = pa.Y.CompareTo(pb.Y);
            return c != 0 ? c : pa.X.CompareTo(pb.X);
        });

        int? titleIdx = null;
        foreach (var idx in order)
        {
            var e = elements[idx];
            if (e.Kind == SlideKind.Text && e.IsTitle)
            {
                var plainText = JoinRuns(e.Runs, false);
                if (plainText.Trim().Length > 0) { titleIdx = idx; break; }
            }
        }
        if (titleIdx is null)
        {
            foreach (var idx in order)
            {
                var e = elements[idx];
                if (e.Kind == SlideKind.Text)
                {
                    var plainText = JoinRuns(e.Runs, false);
                    var normalized = plainText.Replace("\n", " ");
                    if (Encoding.UTF8.GetByteCount(normalized) < 100 && normalized.Trim().Length > 0) { titleIdx = idx; break; }
                }
            }
        }

        if (titleIdx is not null)
        {
            var e = elements[titleIdx.Value];
            var textContent = JoinRuns(e.Runs, !plain);
            builder.AddTitle(textContent.Replace("\n", " ").Trim());
        }

        foreach (var idx in order)
        {
            if (idx == titleIdx) continue;
            var e = elements[idx];
            switch (e.Kind)
            {
                case SlideKind.Text:
                    builder.AddText(JoinRuns(e.Runs, !plain));
                    break;
                case SlideKind.Table:
                    var rows = e.TableRows.Select(row => row.Select(cell => JoinRuns(cell, !plain)).ToList()).ToList();
                    builder.AddTable(rows);
                    break;
                case SlideKind.List:
                    foreach (var item in e.Items)
                    {
                        var itemText = JoinRuns(item.Runs, !plain);
                        if (item.HasBullet) builder.AddListItem(item.Level, item.IsOrdered, itemText);
                        else builder.AddText(itemText);
                    }
                    break;
                case SlideKind.Image:
                    if (injectPlaceholders) builder.AddImageWithDesc(e.Description, e.Target ?? "");
                    break;
            }
        }
        return builder.Build();
    }

    private static string JoinRuns(List<Run> runs, bool asMarkdown)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            var text = asMarkdown ? run.RenderAsMd() : run.Text;
            if (sb.Length > 0 && text.Length > 0)
            {
                bool endsWs = char.IsWhiteSpace(sb[^1]);
                bool startsWs = char.IsWhiteSpace(text[0]);
                if (!endsWs && !startsWs) sb.Append(' ');
            }
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static void ResolveImageTargets(OoxmlPackage pkg, string slidePath, List<SlideElement> elements)
    {
        if (!elements.Any(e => e.Kind == SlideKind.Image)) return;
        int slash = slidePath.LastIndexOf('/');
        string dir = slash >= 0 ? slidePath[..(slash + 1)] : "";
        string file = slash >= 0 ? slidePath[(slash + 1)..] : slidePath;
        var relsXml = pkg.ReadXml($"{dir}_rels/{file}.rels");
        if (relsXml?.Root is null) return;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in relsXml.Root.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            var type = r.Attribute("Type")?.Value ?? "";
            var id = r.Attribute("Id")?.Value;
            var target = r.Attribute("Target")?.Value;
            if (id is not null && target is not null && type.Contains("image")) map[id] = target;
        }
        foreach (var e in elements)
            if (e.Kind == SlideKind.Image && e.ImageId is not null && map.TryGetValue(e.ImageId, out var t))
                e.Target = t;
    }

    // ── notes ──────────────────────────────────────────────────────────────────
    private static Dictionary<uint, string> ExtractAllNotes(OoxmlPackage pkg, List<string> slidePaths)
    {
        var notes = new Dictionary<uint, string>();
        for (int i = 0; i < slidePaths.Count; i++)
        {
            var notesPath = slidePaths[i].Replace("slides/slide", "notesSlides/notesSlide");
            var xml = pkg.ReadXml(notesPath);
            if (xml?.Root is null) continue;
            var texts = xml.Root.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value);
            notes[(uint)(i + 1)] = string.Join(" ", texts);
        }
        return notes;
    }

    // ── metadata ───────────────────────────────────────────────────────────────
    private static void BuildOfficeMetadata(PptxResult result, CoreProperties core, AppProperties app, OoxmlPackage pkg)
    {
        var m = result.OfficeMetadata;
        if (core.Title is not null) m["title"] = core.Title;
        if (core.Creator is not null) { m["author"] = core.Creator; m["created_by"] = core.Creator; }
        if (core.Subject is not null) { m["subject"] = core.Subject; m["summary"] = core.Subject; }
        if (core.Keywords is not null) m["keywords"] = core.Keywords;
        if (core.Description is not null) m["description"] = core.Description;
        if (core.LastModifiedBy is not null) m["modified_by"] = core.LastModifiedBy;
        if (core.Created is not null) m["created_at"] = core.Created;
        if (core.Modified is not null) m["modified_at"] = core.Modified;
        if (core.Revision is not null) m["revision"] = core.Revision;
        if (core.Category is not null) m["category"] = core.Category;

        if (app.Slides is not null) { m["slide_count"] = app.Slides.Value.ToString(CultureInfo.InvariantCulture); result.AppSlideCount = (uint)Math.Max(0, app.Slides.Value); }
        if (app.Notes is not null) m["notes_count"] = app.Notes.Value.ToString(CultureInfo.InvariantCulture);
        if (app.HiddenSlides is not null) m["hidden_slides"] = app.HiddenSlides.Value.ToString(CultureInfo.InvariantCulture);
        if (app.TitlesOfParts.Count > 0) { result.SlideNames = new List<string>(app.TitlesOfParts); m["slide_titles"] = string.Join(", ", app.TitlesOfParts); }
        if (app.PresentationFormat is not null) m["presentation_format"] = app.PresentationFormat;
        if (app.Company is not null) m["organization"] = app.Company;
        if (app.Application is not null) m["application"] = app.Application;
        if (app.AppVersion is not null) m["application_version"] = app.AppVersion;
        // #230: surface the raw DocSecurity integer plus its decoded ECMA-376 flags —
        // PptxAppProperties never reaches the format metadata, so without this the
        // presentation's protection state is discarded entirely.
        if (app.DocSecurity is { } docSecurity)
        {
            m[OfficeMetadata.DocSecurityKey] = docSecurity.ToString(CultureInfo.InvariantCulture);
            foreach (var (key, value) in OfficeMetadata.DecodeDocSecurityFlags(docSecurity))
                m[key] = value ? "true" : "false";
        }

        foreach (var (k, v) in OfficeMetadata.ExtractCustom(pkg))
            m["custom_" + k] = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
    }

    // ── content builder (ports pptx ContentBuilder) ─────────────────────────────
    private sealed class ContentBuilder
    {
        private readonly bool _plain;
        private readonly StringBuilder _c = new();
        public ContentBuilder(bool plain) => _plain = plain;

        private bool EndsWith(string s)
        {
            if (_c.Length < s.Length) return false;
            for (int i = 0; i < s.Length; i++)
                if (_c[_c.Length - s.Length + i] != s[i]) return false;
            return true;
        }

        public void AddText(string text)
        {
            if (text.Trim().Length == 0) return;
            if (_c.Length > 0 && !EndsWith("\n\n"))
            {
                if (!EndsWith("\n")) _c.Append('\n');
                _c.Append('\n');
            }
            _c.Append(text);
            if (!EndsWith("\n")) _c.Append('\n');
        }

        public void AddTitle(string title)
        {
            if (title.Trim().Length == 0) return;
            if (!_plain) _c.Append("# ");
            _c.Append(title.Trim());
            _c.Append("\n\n");
        }

        public void AddTable(List<List<string>> rows)
        {
            if (rows.Count == 0) return;
            int numCols = rows.Max(r => r.Count);
            if (numCols == 0) return;
            _c.Append('\n');
            if (_plain)
            {
                foreach (var row in rows)
                {
                    for (int i = 0; i < row.Count; i++)
                    {
                        if (i > 0) _c.Append('\t');
                        _c.Append(row[i]);
                    }
                    _c.Append('\n');
                }
            }
            else
            {
                var widths = new int[numCols];
                for (int i = 0; i < numCols; i++) widths[i] = 3;
                foreach (var row in rows)
                    for (int i = 0; i < row.Count; i++)
                        widths[i] = Math.Max(widths[i], Encoding.UTF8.GetByteCount(row[i]));
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    _c.Append('|');
                    for (int i = 0; i < row.Count; i++)
                        _c.Append(' ').Append(row[i].PadRight(widths[i])).Append(" |");
                    for (int i = row.Count; i < numCols; i++)
                        _c.Append(' ').Append("".PadRight(widths[i])).Append(" |");
                    _c.Append('\n');
                    if (r == 0)
                    {
                        _c.Append('|');
                        for (int i = 0; i < numCols; i++)
                            _c.Append(' ').Append(new string('-', widths[i])).Append(" |");
                        _c.Append('\n');
                    }
                }
            }
        }

        public void AddListItem(uint level, bool ordered, string text)
        {
            if (!_plain)
            {
                for (int k = 0; k < (int)Math.Max(0, (long)level - 1); k++) _c.Append("  ");
                _c.Append(ordered ? "1." : "-");
                _c.Append(' ');
            }
            _c.Append(text.Trim());
            _c.Append('\n');
        }

        public void AddImageWithDesc(string? description, string target)
        {
            if (_plain) return;
            var alt = (description ?? "").Replace("\n", " ").Replace("\r", "");
            if (_c.Length > 0 && !EndsWith("\n")) _c.Append('\n');
            _c.Append($"![{alt.Trim()}]({target})\n");
        }

        public void AddNotes(string notes)
        {
            if (notes.Trim().Length == 0) return;
            _c.Append(_plain ? "\n\nNotes:\n" : "\n\n### Notes:\n");
            _c.Append(notes);
            _c.Append('\n');
        }

        public string Build() => _c.ToString().Trim();
    }

    private struct Position { public long X, Y, Cx, Cy; }

    private enum SlideKind { Text, Table, Image, List, Unknown }

    private sealed class Run
    {
        public string Text = "";
        public bool Bold, Italic, Underline, Strike;
        public string RenderAsMd()
        {
            var result = Text;
            if (Bold) result = $"**{result}**";
            if (Italic) result = $"*{result}*";
            if (Underline) result = $"<u>{result}</u>";
            if (Strike) result = $"~~{result}~~";
            return result;
        }
    }

    private sealed class ListItem
    {
        public uint Level;
        public bool IsOrdered, HasBullet;
        public List<Run> Runs = new();
    }

    private sealed class SlideElement
    {
        public SlideKind Kind;
        public Position Pos;
        public bool IsTitle;
        public List<Run> Runs = new();
        public List<ListItem> Items = new();
        public List<List<List<Run>>> TableRows = new();
        public string? ImageId;
        public string? Target;
        public string? Description;
    }
}
