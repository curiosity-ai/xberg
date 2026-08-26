using System.Text.Json.Serialization;

namespace Xberg.Core;

/// <summary>Built-in stylesheet for <see cref="HtmlOutputConfig"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HtmlTheme>))]
public enum HtmlTheme
{
    /// <summary>System font stack, neutral colours, readable measure. Every <c>--kb-*</c>
    /// custom property is defined, so user CSS can override values one at a time.</summary>
    [JsonStringEnumMemberName("default")] Default,

    /// <summary>GitHub Markdown's palette and spacing.</summary>
    [JsonStringEnumMemberName("github")] GitHub,

    /// <summary>Dark background, light text.</summary>
    [JsonStringEnumMemberName("dark")] Dark,

    /// <summary>A light theme with generous whitespace.</summary>
    [JsonStringEnumMemberName("light")] Light,

    /// <summary>No stylesheet at all. The custom properties are still defined on
    /// <c>:root</c>, so a user stylesheet can reference the <c>var(--kb-*)</c> tokens.</summary>
    [JsonStringEnumMemberName("unstyled")] Unstyled,
}

/// <summary>
/// How <c>OutputFormat.Html</c> renders a document: which theme, whether the CSS is embedded,
/// and any stylesheet of the caller's own.
/// </summary>
/// <remarks>
/// Setting this on <see cref="ExtractionConfig.HtmlOutput"/> alongside
/// <c>OutputFormat.Html</c> selects <see cref="Xberg.Rendering.StyledHtmlRenderer"/> in place of
/// the markdown-based renderer. Every emitted class name and every <c>--kb-*</c> custom property
/// is part of a stability contract — see upstream's
/// <c>docs/reference/html-styling-contract.md</c>.
/// </remarks>
public sealed class HtmlOutputConfig
{
    /// <summary>CSS injected after the theme stylesheet. Concatenated after
    /// <see cref="CssFile"/> when both are set.</summary>
    [JsonPropertyName("css")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Css { get; set; }

    /// <summary>A stylesheet read from disk once, when the renderer is built. Concatenated
    /// before <see cref="Css"/> when both are set.</summary>
    [JsonPropertyName("css_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CssFile { get; set; }

    /// <summary>Which built-in stylesheet to use. Defaults to <see cref="HtmlTheme.Unstyled"/>.</summary>
    [JsonPropertyName("theme")]
    public HtmlTheme Theme { get; set; } = HtmlTheme.Unstyled;

    /// <summary>
    /// Prefix on every emitted class name. Change it if the host application already uses
    /// classes starting with <c>kb-</c>.
    /// </summary>
    [JsonPropertyName("class_prefix")]
    public string ClassPrefix { get; set; } = "kb-";

    /// <summary>
    /// Whether to write the resolved CSS into a <c>&lt;style&gt;</c> block just inside the
    /// opening wrapper. Turn it off to emit only the markup and supply a stylesheet that
    /// targets the <c>kb-*</c> names.
    /// </summary>
    [JsonPropertyName("embed_css")]
    public bool EmbedCss { get; set; } = true;
}
