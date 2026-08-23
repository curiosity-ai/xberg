// Ported from Rust `crates/xberg/src/extractors/odt.rs`
// (OdtStyleProps + build_style_map).

using System.Xml.Linq;

namespace Xberg.Internal.Odf;

/// <summary>Resolved formatting properties for a text style. Mirrors Rust `OdtStyleProps`.</summary>
internal sealed class OdtStyleProps
{
    public bool Bold;
    public bool Italic;
    public bool Underline;
    public bool Strikethrough;
    public string? Color;
    public string? FontSize;
}

/// <summary>
/// Helpers for resolving ODF automatic styles. Ports Rust `build_style_map`, which parses
/// <c>&lt;style:style&gt;</c> elements from the <c>office:automatic-styles</c>/<c>office:styles</c>
/// sections of content.xml and resolves <c>style:text-properties</c> attributes.
/// </summary>
internal static class OdfStyles
{
    /// <summary>Get an attribute value by local name, ignoring namespace (mirrors roxmltree resolution).</summary>
    internal static string? Attr(XElement el, string localName)
    {
        foreach (var a in el.Attributes())
            if (a.Name.LocalName == localName)
                return a.Value;
        return null;
    }

    /// <summary>Build a map from style-name to resolved formatting properties. Mirrors Rust `build_style_map`.</summary>
    /// <summary>
    /// Map each declared list style's name to whether it numbers its items.
    /// </summary>
    /// <remarks>
    /// A list element says only which style it uses; whether that style is numbered or bulleted
    /// is declared once, here, by whether any of its levels is a
    /// <c>text:list-level-style-number</c>. Without the map every list reads as bulleted, which
    /// silently rewrites every numbered list in every document.
    /// </remarks>
    public static Dictionary<string, bool> BuildListStyleMap(XElement root)
    {
        var styles = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var child in root.Elements())
        {
            if (child.Name.LocalName is not ("automatic-styles" or "styles")) continue;
            foreach (var styleNode in child.Elements())
            {
                if (styleNode.Name.LocalName != "list-style") continue;
                string? name = Attr(styleNode, "name");
                if (name is null) continue;
                styles[name] = styleNode.Elements().Any(l => l.Name.LocalName == "list-level-style-number");
            }
        }
        return styles;
    }

    public static Dictionary<string, OdtStyleProps> BuildStyleMap(XElement root)
    {
        var styles = new Dictionary<string, OdtStyleProps>();
        foreach (var child in root.Elements())
        {
            if (child.Name.LocalName != "automatic-styles" && child.Name.LocalName != "styles")
                continue;

            foreach (var styleNode in child.Elements())
            {
                if (styleNode.Name.LocalName != "style")
                    continue;
                var name = Attr(styleNode, "name");
                if (name is null)
                    continue;

                var props = new OdtStyleProps();
                foreach (var propChild in styleNode.Elements())
                {
                    if (propChild.Name.LocalName != "text-properties")
                        continue;

                    // Bold: fo:font-weight="bold"
                    var fw = Attr(propChild, "font-weight");
                    if (fw is not null)
                        props.Bold = fw == "bold";

                    // Italic: fo:font-style="italic"
                    var fs = Attr(propChild, "font-style");
                    if (fs is not null)
                        props.Italic = fs == "italic";

                    // Underline: style:text-underline-style != "none"
                    var ul = Attr(propChild, "text-underline-style");
                    if (ul is not null)
                        props.Underline = ul != "none";

                    // Strikethrough: style:text-line-through-style != "none"
                    var st = Attr(propChild, "text-line-through-style");
                    if (st is not null)
                        props.Strikethrough = st != "none";

                    // Color: fo:color="#rrggbb" (ignore black)
                    var color = Attr(propChild, "color");
                    if (color is not null && color != "#000000")
                        props.Color = color;

                    // Font size: fo:font-size="12pt"
                    var size = Attr(propChild, "font-size");
                    if (size is not null)
                        props.FontSize = size;
                }

                styles[name] = props;
            }
        }
        return styles;
    }
}
