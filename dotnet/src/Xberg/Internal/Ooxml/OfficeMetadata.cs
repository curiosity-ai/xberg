using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Xberg.Internal.Ooxml;

/// <summary>Dublin Core metadata from docProps/core.xml.</summary>
public sealed class CoreProperties
{
    public string? Title, Subject, Creator, Keywords, Description, LastModifiedBy, Revision,
        Created, Modified, Category, ContentStatus, Language, Identifier, Version, LastPrinted;
}

/// <summary>Application properties from docProps/app.xml (superset for docx/xlsx/pptx).</summary>
public sealed class AppProperties
{
    public string? Application, AppVersion, Template, Company, PresentationFormat;
    public int? TotalTime, Pages, Words, Characters, CharactersWithSpaces, Lines, Paragraphs,
        DocSecurity, Slides, Notes, HiddenSlides, MultimediaClips;
    public bool? ScaleCrop, LinksUpToDate, SharedDoc, HyperlinksChanged;

    /// <summary>
    /// Every <c>vt:lpstr</c> under <c>TitlesOfParts</c>'s vector, in document order, empties
    /// included.
    /// <para>
    /// This is one flat vector concatenating several logical groups — fonts used, the theme,
    /// and the slide or worksheet names — so on its own it is not a list of anything in
    /// particular. <see cref="HeadingPairs"/> says where each group starts and ends, and empty
    /// entries are dropped only <em>after</em> slicing, since removing them earlier would shift
    /// every later group's offset.
    /// </para>
    /// </summary>
    public List<string> TitlesOfParts { get; } = new();

    /// <summary>
    /// The <c>HeadingPairs</c> vector as (group name, entry count), in document order. Each
    /// pair claims that many consecutive entries of <see cref="TitlesOfParts"/>.
    /// </summary>
    public List<(string Name, int Count)> HeadingPairs { get; } = new();

    /// <summary>
    /// The <see cref="TitlesOfParts"/> entries belonging to the first heading group whose name
    /// contains <paramref name="needle"/>, compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// With no heading pairs at all there is nothing to slice by, so the whole vector is
    /// returned; with heading pairs present but none matching, the group genuinely is not there
    /// and the answer is empty rather than everything.
    /// </remarks>
    public List<string> TitlesForHeading(string needle)
    {
        if (HeadingPairs.Count == 0)
            return TitlesOfParts.Where(t => t.Length > 0).ToList();

        int offset = 0;
        foreach (var (name, count) in HeadingPairs)
        {
            int start = Math.Min(offset, TitlesOfParts.Count);
            int end = Math.Min(offset + count, TitlesOfParts.Count);
            if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return TitlesOfParts.GetRange(start, end - start).Where(t => t.Length > 0).ToList();
            offset = end;
        }
        return new List<string>();
    }
}

/// <summary>
/// Ports <c>extraction/office_metadata</c>: reads core/app/custom properties from an OOXML package.
/// Namespace-agnostic (matches roxmltree's local-name tag matching).
/// </summary>
public static class OfficeMetadata
{
    /// <summary>Metadata key carrying the raw, undecoded <c>DocSecurity</c> integer.</summary>
    public const string DocSecurityKey = "doc_security";

    /// <summary>
    /// Decode a <c>DocSecurity</c> bit field into named boolean flags.
    /// ECMA-376 Part 1 §22.2.2.7 (as clarified by MS-OI29500) packs four independent
    /// restrictions into one integer: 1 = password protected, 2 = read-only recommended,
    /// 4 = read-only enforced, 8 = locked for annotation. Bits above 8 are not part of the
    /// schema and are ignored.
    /// <para>
    /// All four flags are always returned, including when <paramref name="raw"/> is 0: an
    /// explicit <c>false</c> records that the document <em>declares</em> no restrictions, as
    /// opposed to the "no DocSecurity element at all" case, where the caller decodes nothing.
    /// </para>
    /// </summary>
    public static (string Key, bool Value)[] DecodeDocSecurityFlags(int raw)
    {
        const int PasswordProtectedBit = 1;
        const int ReadOnlyRecommendedBit = 2;
        const int ReadOnlyEnforcedBit = 4;
        const int LockedForAnnotationsBit = 8;

        return new[]
        {
            ("doc_security_password_protected", (raw & PasswordProtectedBit) != 0),
            ("doc_security_read_only_recommended", (raw & ReadOnlyRecommendedBit) != 0),
            ("doc_security_read_only_enforced", (raw & ReadOnlyEnforcedBit) != 0),
            ("doc_security_locked_for_annotations", (raw & LockedForAnnotationsBit) != 0),
        };
    }

    public static CoreProperties ExtractCore(OoxmlPackage pkg)
    {
        var props = new CoreProperties();
        var doc = pkg.ReadXml("docProps/core.xml");
        if (doc?.Root is null) return props;
        var r = doc.Root;
        props.Title = Text(r, "title");
        props.Subject = Text(r, "subject");
        props.Creator = Text(r, "creator");
        props.Description = Text(r, "description");
        props.Language = Text(r, "language");
        props.Identifier = Text(r, "identifier");
        props.Keywords = Text(r, "keywords");
        props.LastModifiedBy = Text(r, "lastModifiedBy");
        props.Revision = Text(r, "revision");
        props.Category = Text(r, "category");
        props.ContentStatus = Text(r, "contentStatus");
        props.Version = Text(r, "version");
        props.Created = Text(r, "created");
        props.Modified = Text(r, "modified");
        props.LastPrinted = Text(r, "lastPrinted");
        return props;
    }

    public static AppProperties ExtractApp(OoxmlPackage pkg)
    {
        var props = new AppProperties();
        var doc = pkg.ReadXml("docProps/app.xml");
        if (doc?.Root is null) return props;
        var r = doc.Root;
        props.Application = Text(r, "Application");
        props.AppVersion = Text(r, "AppVersion");
        props.Template = Text(r, "Template");
        props.Company = Text(r, "Company");
        props.PresentationFormat = Text(r, "PresentationFormat");
        props.TotalTime = Int(r, "TotalTime");
        props.Pages = Int(r, "Pages");
        props.Words = Int(r, "Words");
        props.Characters = Int(r, "Characters");
        props.CharactersWithSpaces = Int(r, "CharactersWithSpaces");
        props.Lines = Int(r, "Lines");
        props.Paragraphs = Int(r, "Paragraphs");
        props.DocSecurity = Int(r, "DocSecurity");
        props.Slides = Int(r, "Slides");
        props.Notes = Int(r, "Notes");
        props.HiddenSlides = Int(r, "HiddenSlides");
        props.MultimediaClips = Int(r, "MMClips");
        props.ScaleCrop = Bool(r, "ScaleCrop");
        props.LinksUpToDate = Bool(r, "LinksUpToDate");
        props.SharedDoc = Bool(r, "SharedDoc");
        props.HyperlinksChanged = Bool(r, "HyperlinksChanged");
        // TitlesOfParts/vector/lpstr — kept raw, empties and all; see AppProperties.TitlesOfParts.
        var titles = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "TitlesOfParts");
        if (titles is not null)
        {
            var vector = titles.Descendants().FirstOrDefault(e => e.Name.LocalName == "vector");
            if (vector is not null)
                foreach (var lp in vector.Elements().Where(e => e.Name.LocalName == "lpstr"))
                    props.TitlesOfParts.Add((lp.Value ?? "").Trim());
        }

        // HeadingPairs/vector/variant — alternating name (lpstr) and count (i4).
        var headings = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "HeadingPairs");
        var headingVector = headings?.Descendants().FirstOrDefault(e => e.Name.LocalName == "vector");
        if (headingVector is not null)
        {
            var variants = headingVector.Elements().Where(e => e.Name.LocalName == "variant").ToList();
            for (int i = 0; i + 1 < variants.Count; i += 2)
            {
                string? name = variants[i].Elements().FirstOrDefault(e => e.Name.LocalName == "lpstr")?.Value?.Trim();
                string? countText = variants[i + 1].Elements().FirstOrDefault(e => e.Name.LocalName == "i4")?.Value?.Trim();
                if (name is not null && int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    props.HeadingPairs.Add((name, count));
            }
        }
        return props;
    }

    /// <summary>Read docProps/custom.xml into a name→JsonElement map.</summary>
    public static Dictionary<string, JsonElement> ExtractCustom(OoxmlPackage pkg)
    {
        var result = new Dictionary<string, JsonElement>();
        var doc = pkg.ReadXml("docProps/custom.xml");
        if (doc?.Root is null) return result;
        foreach (var prop in doc.Root.Descendants().Where(e => e.Name.LocalName == "property"))
        {
            var name = prop.Attribute("name")?.Value;
            if (name is null) continue;
            var val = ExtractVtValue(prop);
            if (val is not null) result[name] = val.Value;
        }
        return result;
    }

    private static JsonElement? ExtractVtValue(XElement node)
    {
        foreach (var child in node.Elements())
        {
            var tag = child.Name.LocalName;
            switch (tag)
            {
                case "lpwstr":
                case "lpstr":
                case "filetime":
                    // roxmltree `text()` yields None for an element with no text node → skip.
                    return child.Value.Length == 0 ? (JsonElement?)null : JsonEl(JsonValueString(child.Value));
                case "i4":
                    if (long.TryParse(child.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                        return JsonEl(i.ToString(CultureInfo.InvariantCulture));
                    return null;
                case "r8":
                    if (double.TryParse(child.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        // serde_json f64 Display keeps a decimal point (e.g. "30197800.0").
                        var s = d.ToString("R", CultureInfo.InvariantCulture);
                        if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
                        return JsonEl(s);
                    }
                    return null;
                case "bool":
                    var b = child.Value.Trim().ToLowerInvariant();
                    if (b is "true" or "1") return JsonEl("true");
                    if (b is "false" or "0") return JsonEl("false");
                    return null;
            }
        }
        return null;
    }

    private static string JsonValueString(string s) => JsonSerializer.Serialize(s);
    private static JsonElement JsonEl(string rawJson) => JsonDocument.Parse(rawJson).RootElement.Clone();

    /// <summary>First descendant with matching local name, trimmed non-empty text.</summary>
    private static string? Text(XElement root, string localName)
    {
        var node = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);
        if (node is null) return null;
        var t = (node.Value ?? "").Trim();
        return t.Length == 0 ? null : t;
    }

    private static int? Int(XElement root, string localName)
    {
        var node = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);
        if (node is null) return null;
        return int.TryParse((node.Value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? Bool(XElement root, string localName)
    {
        var node = root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == localName);
        if (node is null) return null;
        return (node.Value ?? "").Trim().ToLowerInvariant() switch { "true" => true, "false" => false, _ => (bool?)null };
    }
}
