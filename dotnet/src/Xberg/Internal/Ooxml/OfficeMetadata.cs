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
    public List<string> TitlesOfParts { get; } = new();
}

/// <summary>
/// Ports <c>extraction/office_metadata</c>: reads core/app/custom properties from an OOXML package.
/// Namespace-agnostic (matches roxmltree's local-name tag matching).
/// </summary>
public static class OfficeMetadata
{
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
        // TitlesOfParts/vector/lpstr
        var titles = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "TitlesOfParts");
        if (titles is not null)
        {
            var vector = titles.Descendants().FirstOrDefault(e => e.Name.LocalName == "vector");
            if (vector is not null)
                foreach (var lp in vector.Descendants().Where(e => e.Name.LocalName == "lpstr"))
                {
                    var t = (lp.Value ?? "").Trim();
                    if (t.Length > 0) props.TitlesOfParts.Add(t);
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
