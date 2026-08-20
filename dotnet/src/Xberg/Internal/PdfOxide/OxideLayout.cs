// Ported from pdf_oxide `layout/text_block.rs` (TextSpan, TextChar, PageText, Color,
// FontWeight) and `structure/types.rs` (McidScope).
//
// TextSpan is the unit the whole downstream pipeline is calibrated to: spacing,
// reading order and paragraph breaks are all tuned to the granularity the span
// merger produces, so the field set is kept complete even where xberg reads only
// part of it.
using System;
using System.Collections.Generic;

namespace Xberg.Internal.PdfOxide;

public enum OxFontWeight
{
    Thin = 100,
    ExtraLight = 200,
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800,
    Black = 900,
}

public readonly struct OxColor
{
    public readonly float R, G, B;
    public OxColor(float r, float g, float b) { R = r; G = g; B = b; }
    public static OxColor Black => new(0, 0, 0);
    public static OxColor White => new(1, 1, 1);
}

/// <summary>
/// Which content stream an MCID belongs to (ISO 32000-1 §14.7.4.3). MCIDs are scoped to
/// one stream, so two Form XObjects on a page can each carry MCID 0 without colliding.
/// </summary>
public readonly struct OxMcidScope : IEquatable<OxMcidScope>
{
    public enum Kind { Page, Form, Pattern }

    public readonly Kind ScopeKind;
    /// <summary>Page index for <see cref="Kind.Page"/>, object number otherwise.</summary>
    public readonly int Id;
    public readonly int Generation;

    private OxMcidScope(Kind kind, int id, int generation) { ScopeKind = kind; Id = id; Generation = generation; }

    public static OxMcidScope Page(int pageIndex) => new(Kind.Page, pageIndex, 0);
    public static OxMcidScope Form(int number, int generation) => new(Kind.Form, number, generation);
    public static OxMcidScope Pattern(int number, int generation) => new(Kind.Pattern, number, generation);

    public int? PageIndex => ScopeKind == Kind.Page ? Id : null;

    public bool Equals(OxMcidScope other) =>
        ScopeKind == other.ScopeKind && Id == other.Id && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is OxMcidScope o && Equals(o);
    public override int GetHashCode() => HashCode.Combine((int)ScopeKind, Id, Generation);
}

/// <summary>Artifact classification for content marked outside the logical flow (§14.8.2.2).</summary>
public enum OxArtifactType { Pagination, Layout, Page, Background }

public enum OxPaginationSubtype { Header, Footer, Watermark }

/// <summary>One positioned glyph.</summary>
public sealed class OxTextChar
{
    public char Char;
    public OxRect Bbox;
    public string FontName = "Helvetica";
    public float FontSize = 12.0f;
    public OxFontWeight FontWeight = OxFontWeight.Normal;
    public bool IsItalic;
    public bool IsMonospace;
    public OxColor Color = OxColor.Black;
    public int? Mcid;

    /// <summary>Baseline origin, which is not the bbox corner for glyphs with descenders.</summary>
    public float OriginX;
    public float OriginY;
    public float RotationDegrees;
    public float AdvanceWidth;
    public float RenderedAdvance;
    public float Ascent;
    public float Descent;
    public float[]? Matrix;
}

/// <summary>A run of glyphs sharing one text state, as the span merger left it.</summary>
public sealed class OxTextSpan
{
    public string Text = "";
    public OxRect Bbox;
    public string FontName = "Helvetica";
    public float FontSize = 12.0f;
    public OxFontWeight FontWeight = OxFontWeight.Normal;
    public bool IsItalic;
    public bool IsMonospace;
    public OxColor Color = OxColor.Black;
    public int? Mcid;
    public OxMcidScope? McidScope;
    public int Sequence;

    /// <summary>Set when the span was cut out of a fused word.</summary>
    public bool SplitBoundaryBefore;
    /// <summary>Set when the TJ processor synthesised this span as a space.</summary>
    public bool OffsetSemantic;

    public float CharSpacing;
    public float WordSpacing;
    public float HorizontalScaling = 100.0f;
    public bool PrimaryDetected;
    public OxArtifactType? ArtifactType;

    /// <summary>Per-glyph advance widths in user space; empty when unavailable.</summary>
    public List<float> CharWidths = new();
    /// <summary>
    /// Per-glyph baseline origins. Prefix-summing <see cref="CharWidths"/> drifts because the
    /// nominal widths omit TJ kerning (§9.4.3); these carry the real positions.
    /// </summary>
    public List<float> CharXOffsets = new();

    public byte? HeadingLevel;
    /// <summary>From <c>atan2(b, a)</c> of the composed text rendering matrix, quadrant-snapped.</summary>
    public float RotationDegrees;
    /// <summary>0 = horizontal, 1 = vertical (tategaki).</summary>
    public byte Wmode;
    /// <summary>Text rise as a ratio of font size (§9.3.7): positive is superscript.</summary>
    public float TextRise;
    /// <summary>
    /// True when the glyphs were drawn right-to-left, i.e. the producer stored RTL text in
    /// logical order and positioned each glyph (§14.8.2.3.3 method 1), so the characters
    /// must not be reversed again downstream.
    /// </summary>
    public bool RtlDrawLogical;

    /// <summary>
    /// A copy whose per-glyph lists are its own. The merge pass rewrites
    /// <see cref="CharWidths"/> in place, so a shallow copy would let a span corrupt the
    /// one it was cloned from — which is what Rust's derived Clone avoids by deep-copying
    /// its Vecs.
    /// </summary>
    public OxTextSpan Clone()
    {
        var copy = (OxTextSpan)MemberwiseClone();
        copy.CharWidths = new List<float>(CharWidths);
        copy.CharXOffsets = new List<float>(CharXOffsets);
        return copy;
    }
}

public sealed class OxPageText
{
    public List<OxTextSpan> Spans = new();
    public List<OxTextChar> Chars = new();
    public float PageWidth;
    public float PageHeight;
}
