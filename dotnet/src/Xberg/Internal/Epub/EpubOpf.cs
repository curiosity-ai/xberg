// Ported from crates/xberg/src/extractors/epub/metadata.rs
// OPF (Open Packaging Format) parsing: Dublin Core metadata, manifest, spine order.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xberg.Types;

namespace Xberg.Internal.Epub;

/// <summary>Metadata extracted from the OPF file. Mirrors Rust `OepbMetadata`.</summary>
internal sealed class OpfMetadata
{
    public string? Title { get; set; }
    public string? Creator { get; set; }
    public string? Date { get; set; }
    public string? Language { get; set; }
    public string? Identifier { get; set; }
    public string? Publisher { get; set; }
    public string? Subject { get; set; }
    public string? Description { get; set; }
    public string? Rights { get; set; }
    public string? Coverage { get; set; }
    public string? Format { get; set; }
    public string? Relation { get; set; }
    public string? Source { get; set; }
    public string? DcType { get; set; }
    public string? CoverImageHref { get; set; }
}

/// <summary>A spine entry (`&lt;itemref idref="..."/&gt;`). Mirrors Rust `EpubSpineItem`.</summary>
internal sealed class EpubSpineItem
{
    public string Idref { get; set; } = "";
}

/// <summary>A manifest entry. Mirrors Rust `ManifestItem`.</summary>
internal sealed class ManifestItem
{
    public string RawHref { get; set; } = "";
    public string? Path { get; set; }
    public string? PathResolutionError { get; set; }
    public string? MediaType { get; set; }
    public string? Fallback { get; set; }
    public string? Properties { get; set; }

    /// <summary>Mirrors `is_renderable_body_document`.</summary>
    public bool IsRenderableBodyDocument() =>
        MediaType is "application/xhtml+xml" or "application/x-dtbook+xml"
        || (MediaType is null && HasRenderableExtension(RawHref));

    /// <summary>Mirrors `resolved_path`: the resolved path, or null with an error message.</summary>
    public (string? Path, string Error) ResolvedPath()
    {
        if (Path is not null) return (Path, "");
        return (null, PathResolutionError ?? $"unable to resolve manifest href '{RawHref}'");
    }

    private static bool HasRenderableExtension(string href)
    {
        int hash = href.IndexOf('#');
        string path = hash >= 0 ? href.Substring(0, hash) : href;
        int slash = path.LastIndexOf('/');
        if (slash >= 0) path = path.Substring(slash + 1);
        int dot = path.LastIndexOf('.');
        if (dot < 0) return false;
        string ext = path.Substring(dot + 1).ToLowerInvariant();
        return ext is "xhtml" or "html" or "htm" or "xml" or "dtbook";
    }
}

/// <summary>Parsed OPF package document. Mirrors Rust `EpubPackageDocument`.</summary>
internal sealed class EpubPackageDocument
{
    public OpfMetadata Metadata { get; set; } = new();
    public Dictionary<string, ManifestItem> Manifest { get; set; } = new(StringComparer.Ordinal);
    public List<EpubSpineItem> SpineItems { get; set; } = new();
    public HashSet<string> GuideTocPaths { get; set; } = new(StringComparer.Ordinal);

    public bool IsGuideTocCandidatePath(string path) => GuideTocPaths.Contains(path);
}

internal static class EpubOpf
{
    /// <summary>Parse the OPF file, extracting metadata, manifest and spine order. Mirrors `parse_opf`.</summary>
    public static (EpubPackageDocument Package, List<ProcessingWarning> Warnings) ParseOpf(string xml, string opfDir)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e)
        {
            throw new EpubParseException($"Failed to parse OPF file: {e.Message}");
        }

        var warnings = new List<ProcessingWarning>();
        var package = new EpubPackageDocument();
        var manifest = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        var meta = package.Metadata;

        foreach (var node in doc.Descendants())
        {
            switch (node.Name.LocalName)
            {
                case "title": SetIfText(node, v => meta.Title = v); break;
                case "creator": SetIfText(node, v => meta.Creator = v); break;
                case "date": SetIfText(node, v => meta.Date = v); break;
                case "language": SetIfText(node, v => meta.Language = v); break;
                case "identifier": SetIfText(node, v => meta.Identifier = v); break;
                case "publisher": SetIfText(node, v => meta.Publisher = v); break;
                case "subject": SetIfText(node, v => meta.Subject = v); break;
                case "description": SetIfText(node, v => meta.Description = v); break;
                case "rights": SetIfText(node, v => meta.Rights = v); break;
                case "coverage": SetIfText(node, v => meta.Coverage = v); break;
                case "format": SetIfText(node, v => meta.Format = v); break;
                case "relation": SetIfText(node, v => meta.Relation = v); break;
                case "source": SetIfText(node, v => meta.Source = v); break;
                case "type": SetIfText(node, v => meta.DcType = v); break;
                case "item":
                {
                    string? id = node.Attribute("id")?.Value;
                    string? href = node.Attribute("href")?.Value;
                    if (id is not null && href is not null)
                    {
                        string? path;
                        string? resolutionError;
                        if (EpubContainer.TryResolvePath(opfDir, href, out var resolved, out var err))
                        {
                            path = resolved.Path;
                            resolutionError = null;
                        }
                        else
                        {
                            path = null;
                            resolutionError = err;
                        }
                        manifest[id] = new ManifestItem
                        {
                            RawHref = href,
                            Path = path,
                            PathResolutionError = resolutionError,
                            MediaType = node.Attribute("media-type")?.Value,
                            Fallback = node.Attribute("fallback")?.Value,
                            Properties = node.Attribute("properties")?.Value,
                        };
                    }
                    break;
                }
                case "reference":
                {
                    var kind = node.Attribute("type")?.Value;
                    string? href = node.Attribute("href")?.Value;
                    if (kind is not null && kind.Equals("toc", StringComparison.OrdinalIgnoreCase) && href is not null)
                    {
                        if (EpubContainer.TryResolvePath(opfDir, href, out var resolved, out var err))
                            package.GuideTocPaths.Add(resolved.Path);
                        else
                            warnings.Add(new ProcessingWarning
                            {
                                Source = "epub",
                                Message = $"Skipping malformed guide reference '{href}': {err}",
                            });
                    }
                    break;
                }
            }
        }

        // Find cover image via <meta name="cover" content="item-id"/> (after manifest is complete).
        string? coverItemId = null;
        foreach (var node in doc.Descendants())
        {
            if (node.Name.LocalName == "meta"
                && node.Attribute("name")?.Value == "cover")
            {
                var content = node.Attribute("content")?.Value;
                if (content is not null)
                {
                    coverItemId = content;
                    break;
                }
            }
        }

        if (coverItemId is not null && manifest.TryGetValue(coverItemId, out var coverItem))
        {
            var (path, _) = coverItem.ResolvedPath();
            if (path is not null)
                meta.CoverImageHref = path;
        }

        // Spine order (document order of <itemref idref="..."/>).
        foreach (var node in doc.Descendants())
        {
            if (node.Name.LocalName == "itemref")
            {
                var idref = node.Attribute("idref")?.Value;
                if (idref is not null)
                    package.SpineItems.Add(new EpubSpineItem { Idref = idref });
            }
        }

        package.Manifest = manifest;
        return (package, warnings);
    }

    /// <summary>
    /// Standard Dublin Core fields surfaced into the generic metadata map. Mirrors
    /// `build_additional_metadata`. Values are JSON strings.
    /// </summary>
    public static Dictionary<string, JsonElement> BuildAdditionalMetadata(OpfMetadata meta)
    {
        var additional = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement S(string s) => JsonSerializer.SerializeToElement(s);

        if (meta.Identifier is not null) additional["identifier"] = S(meta.Identifier);
        if (meta.Publisher is not null) additional["publisher"] = S(meta.Publisher);
        if (meta.Subject is not null) additional["subject"] = S(meta.Subject);
        if (meta.Description is not null) additional["description"] = S(meta.Description);
        if (meta.Rights is not null) additional["rights"] = S(meta.Rights);
        return additional;
    }

    /// <summary>
    /// Mirrors Rust `if let Some(text) = node.text() { ...trim... }`: only the element's direct
    /// text content, trimmed, and only assigned when text nodes are present.
    /// </summary>
    private static void SetIfText(XElement node, Action<string> assign)
    {
        var sb = new StringBuilder();
        bool sawText = false;
        foreach (var child in node.Nodes())
        {
            if (child is XText t)
            {
                sb.Append(t.Value);
                sawText = true;
            }
        }
        if (sawText)
            assign(sb.ToString().Trim());
    }
}
