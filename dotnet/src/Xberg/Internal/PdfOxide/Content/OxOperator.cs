// Port of pdf_oxide 0.3.77 `src/content/operators.rs` — the `Operator` enum,
// `TextElement`, `Operator::is_color_setting` and
// `Operator::validate_operands_for_raw_operator`.
//
// Content-stream operands are modelled by `OxOperand` rather than the full PDF
// object model: ISO 32000-1:2008 §7.8.2 forbids stream objects inside a content
// stream, so the shapes an operator can carry are the handful below.

namespace Xberg.Internal.PdfOxide.Content;

/// <summary>An operand value appearing in a content stream (ISO 32000-1 §7.8.2).</summary>
internal abstract record OxOperand
{
    internal sealed record Null : OxOperand
    {
        internal static readonly Null Instance = new();
    }

    internal sealed record Bool(bool Value) : OxOperand;

    internal sealed record Integer(long Value) : OxOperand;

    internal sealed record Real(double Value) : OxOperand;

    /// <summary>A literal or hex string, already unescaped/decoded to raw bytes.</summary>
    internal sealed record Str(byte[] Bytes) : OxOperand;

    /// <summary>A name token, without the leading solidus.</summary>
    internal sealed record Name(string Value) : OxOperand;

    internal sealed record Array(List<OxOperand> Items) : OxOperand;

    internal sealed record Dict(Dictionary<string, OxOperand> Entries) : OxOperand;

    /// <summary>
    /// An indirect reference "n g R". Illegal inside a content stream, but the
    /// object reader performs the same three-token lookahead as the document
    /// parser, so a malformed stream consumes the same bytes either way.
    /// </summary>
    internal sealed record Reference(uint Id, ushort Gen) : OxOperand;

    internal long? AsInteger => this is Integer i ? i.Value : null;

    internal string? AsName => this is Name n ? n.Value : null;

    internal byte[]? AsString => this is Str s ? s.Bytes : null;

    internal List<OxOperand>? AsArray => this is Array a ? a.Items : null;

    /// <summary>Numeric value of an Integer or Real operand; null for every other kind.</summary>
    internal float? AsNumber => this switch
    {
        Integer i => (float)i.Value,
        Real r => (float)r.Value,
        _ => null,
    };
}

/// <summary>An element of a TJ array: either a string to show or a positioning adjustment.</summary>
internal abstract record OxTextElement
{
    /// <summary>Text string to show.</summary>
    internal sealed record Str(byte[] Bytes) : OxTextElement;

    /// <summary>Positioning adjustment, in thousandths of a unit of text space.</summary>
    internal sealed record Offset(float Value) : OxTextElement;
}

/// <summary>
/// A content-stream operator. One sealed record per PDF operator so callers can
/// dispatch with a type-pattern switch, mirroring the Rust `match op { … }`.
/// </summary>
internal abstract record OxOperator
{
    // ── Text positioning ──────────────────────────────────────────────────

    /// <summary>Move text position (Td).</summary>
    internal sealed record Td(float Tx, float Ty) : OxOperator;

    /// <summary>Move text position and set leading (TD).</summary>
    internal sealed record TD(float Tx, float Ty) : OxOperator;

    /// <summary>Set text matrix (Tm).</summary>
    internal sealed record Tm(float A, float B, float C, float D, float E, float F) : OxOperator;

    /// <summary>Move to start of next line (T*).</summary>
    internal sealed record TStar : OxOperator
    {
        internal static readonly TStar Instance = new();
    }

    // ── Text showing ──────────────────────────────────────────────────────

    /// <summary>Show text string (Tj).</summary>
    internal sealed record Tj(byte[] Text) : OxOperator;

    /// <summary>Show text with individual glyph positioning (TJ).</summary>
    internal sealed record TJ(List<OxTextElement> Array) : OxOperator;

    /// <summary>Move to next line and show text (').</summary>
    internal sealed record Quote(byte[] Text) : OxOperator;

    /// <summary>Set spacing and show text (").</summary>
    internal sealed record DoubleQuote(float WordSpace, float CharSpace, byte[] Text) : OxOperator;

    // ── Text state ────────────────────────────────────────────────────────

    /// <summary>Set character spacing (Tc).</summary>
    internal sealed record Tc(float CharSpace) : OxOperator;

    /// <summary>Set word spacing (Tw).</summary>
    internal sealed record Tw(float WordSpace) : OxOperator;

    /// <summary>Set horizontal scaling percentage (Tz).</summary>
    internal sealed record Tz(float Scale) : OxOperator;

    /// <summary>Set text leading (TL).</summary>
    internal sealed record TL(float Leading) : OxOperator;

    /// <summary>Set font and size (Tf).</summary>
    internal sealed record Tf(string Font, float Size) : OxOperator;

    /// <summary>Set text rendering mode (Tr).</summary>
    internal sealed record Tr(byte Render) : OxOperator;

    /// <summary>Set text rise (Ts).</summary>
    internal sealed record Ts(float Rise) : OxOperator;

    // ── Graphics state ────────────────────────────────────────────────────

    /// <summary>Save graphics state (q).</summary>
    internal sealed record SaveState : OxOperator
    {
        internal static readonly SaveState Instance = new();
    }

    /// <summary>Restore graphics state (Q).</summary>
    internal sealed record RestoreState : OxOperator
    {
        internal static readonly RestoreState Instance = new();
    }

    /// <summary>Modify current transformation matrix (cm).</summary>
    internal sealed record Cm(float A, float B, float C, float D, float E, float F) : OxOperator;

    // ── Colour ────────────────────────────────────────────────────────────

    /// <summary>Set RGB fill colour (rg).</summary>
    internal sealed record SetFillRgb(float R, float G, float B) : OxOperator;

    /// <summary>Set RGB stroke colour (RG).</summary>
    internal sealed record SetStrokeRgb(float R, float G, float B) : OxOperator;

    /// <summary>Set gray fill colour (g).</summary>
    internal sealed record SetFillGray(float Gray) : OxOperator;

    /// <summary>Set gray stroke colour (G).</summary>
    internal sealed record SetStrokeGray(float Gray) : OxOperator;

    /// <summary>Set CMYK fill colour (k).</summary>
    internal sealed record SetFillCmyk(float C, float M, float Y, float K) : OxOperator;

    /// <summary>Set CMYK stroke colour (K).</summary>
    internal sealed record SetStrokeCmyk(float C, float M, float Y, float K) : OxOperator;

    /// <summary>Set fill colour space (cs). ISO 32000-1 §8.6.4.</summary>
    internal sealed record SetFillColorSpace(string Name) : OxOperator;

    /// <summary>Set stroke colour space (CS). ISO 32000-1 §8.6.4.</summary>
    internal sealed record SetStrokeColorSpace(string Name) : OxOperator;

    /// <summary>Set fill colour components in the current fill colour space (sc).</summary>
    internal sealed record SetFillColor(List<float> Components) : OxOperator;

    /// <summary>Set stroke colour components in the current stroke colour space (SC).</summary>
    internal sealed record SetStrokeColor(List<float> Components) : OxOperator;

    /// <summary>Set fill colour, optionally naming a pattern (scn). Components may be empty for patterns.</summary>
    internal sealed record SetFillColorN(List<float> Components, string? Name) : OxOperator;

    /// <summary>Set stroke colour, optionally naming a pattern (SCN).</summary>
    internal sealed record SetStrokeColorN(List<float> Components, string? Name) : OxOperator;

    // ── Text objects ──────────────────────────────────────────────────────

    /// <summary>Begin text object (BT).</summary>
    internal sealed record BeginText : OxOperator
    {
        internal static readonly BeginText Instance = new();
    }

    /// <summary>End text object (ET).</summary>
    internal sealed record EndText : OxOperator
    {
        internal static readonly EndText Instance = new();
    }

    // ── XObjects ──────────────────────────────────────────────────────────

    /// <summary>Paint XObject (Do).</summary>
    internal sealed record Do(string Name) : OxOperator;

    // ── Path construction and painting ────────────────────────────────────

    /// <summary>Move to (m).</summary>
    internal sealed record MoveTo(float X, float Y) : OxOperator;

    /// <summary>Line to (l).</summary>
    internal sealed record LineTo(float X, float Y) : OxOperator;

    /// <summary>Cubic Bézier curve (c).</summary>
    internal sealed record CurveTo(float X1, float Y1, float X2, float Y2, float X3, float Y3) : OxOperator;

    /// <summary>Bézier curve with first control point = current point (v).</summary>
    internal sealed record CurveToV(float X2, float Y2, float X3, float Y3) : OxOperator;

    /// <summary>Bézier curve with second control point = end point (y).</summary>
    internal sealed record CurveToY(float X1, float Y1, float X3, float Y3) : OxOperator;

    /// <summary>Close current subpath (h).</summary>
    internal sealed record ClosePath : OxOperator
    {
        internal static readonly ClosePath Instance = new();
    }

    /// <summary>Rectangle (re).</summary>
    internal sealed record Rectangle(float X, float Y, float Width, float Height) : OxOperator;

    /// <summary>Stroke path (S).</summary>
    internal sealed record Stroke : OxOperator
    {
        internal static readonly Stroke Instance = new();
    }

    /// <summary>Fill path, nonzero winding rule (f).</summary>
    internal sealed record Fill : OxOperator
    {
        internal static readonly Fill Instance = new();
    }

    /// <summary>Fill path, even-odd rule (f*).</summary>
    internal sealed record FillEvenOdd : OxOperator
    {
        internal static readonly FillEvenOdd Instance = new();
    }

    /// <summary>Close, fill and stroke (b).</summary>
    internal sealed record CloseFillStroke : OxOperator
    {
        internal static readonly CloseFillStroke Instance = new();
    }

    /// <summary>Fill and stroke, nonzero winding rule (B).</summary>
    internal sealed record FillStroke : OxOperator
    {
        internal static readonly FillStroke Instance = new();
    }

    /// <summary>Fill and stroke, even-odd rule (B*).</summary>
    internal sealed record FillStrokeEvenOdd : OxOperator
    {
        internal static readonly FillStrokeEvenOdd Instance = new();
    }

    /// <summary>Close, fill and stroke, even-odd rule (b*).</summary>
    internal sealed record CloseFillStrokeEvenOdd : OxOperator
    {
        internal static readonly CloseFillStrokeEvenOdd Instance = new();
    }

    /// <summary>End path without filling or stroking (n).</summary>
    internal sealed record EndPath : OxOperator
    {
        internal static readonly EndPath Instance = new();
    }

    /// <summary>Modify clipping path, nonzero winding rule (W).</summary>
    internal sealed record ClipNonZero : OxOperator
    {
        internal static readonly ClipNonZero Instance = new();
    }

    /// <summary>Modify clipping path, even-odd rule (W*).</summary>
    internal sealed record ClipEvenOdd : OxOperator
    {
        internal static readonly ClipEvenOdd Instance = new();
    }

    // ── Non-text graphics state ───────────────────────────────────────────

    /// <summary>Set line width (w).</summary>
    internal sealed record SetLineWidth(float Width) : OxOperator;

    /// <summary>Set line dash pattern (d): [on1 off1 …] phase.</summary>
    internal sealed record SetDash(List<float> Array, float Phase) : OxOperator;

    /// <summary>Set line cap style (J): 0=butt, 1=round, 2=projecting square. ISO 32000-1 §8.4.3.3.</summary>
    internal sealed record SetLineCap(byte CapStyle) : OxOperator;

    /// <summary>Set line join style (j): 0=miter, 1=round, 2=bevel. ISO 32000-1 §8.4.3.4.</summary>
    internal sealed record SetLineJoin(byte JoinStyle) : OxOperator;

    /// <summary>Set miter limit (M). ISO 32000-1 §8.4.3.5.</summary>
    internal sealed record SetMiterLimit(float Limit) : OxOperator;

    /// <summary>Set rendering intent (ri). ISO 32000-1 §8.6.5.8.</summary>
    internal sealed record SetRenderingIntent(string Intent) : OxOperator;

    /// <summary>Set flatness tolerance (i), 0–100. ISO 32000-1 §6.5.1.</summary>
    internal sealed record SetFlatness(float Tolerance) : OxOperator;

    /// <summary>Set extended graphics state from /ExtGState resources (gs). ISO 32000-1 §8.4.5.</summary>
    internal sealed record SetExtGState(string DictName) : OxOperator;

    /// <summary>Paint a shading pattern from /Shading resources (sh). ISO 32000-1 §8.7.4.3.</summary>
    internal sealed record PaintShading(string Name) : OxOperator;

    // ── Inline images ─────────────────────────────────────────────────────

    /// <summary>
    /// A complete BI…ID…EI inline-image sequence (ISO 32000-1 §8.9.7). The
    /// dictionary keeps the abbreviated keys verbatim (W, H, CS, BPC, F, DP, I)
    /// and the data is the raw, still-encoded payload.
    /// </summary>
    internal sealed record InlineImage(Dictionary<string, OxOperand> Dict, byte[] Data) : OxOperator;

    // ── Marked content (ISO 32000-1 §14.6) ────────────────────────────────

    /// <summary>Begin marked content (BMC).</summary>
    internal sealed record BeginMarkedContent(string Tag) : OxOperator;

    /// <summary>
    /// Begin marked content with a property list (BDC). Properties are either an
    /// inline dictionary or a name referencing the /Properties resource.
    /// </summary>
    internal sealed record BeginMarkedContentDict(string Tag, OxOperand Properties) : OxOperator;

    /// <summary>End marked content (EMC).</summary>
    internal sealed record EndMarkedContent : OxOperator
    {
        internal static readonly EndMarkedContent Instance = new();
    }

    // ── Fallback ──────────────────────────────────────────────────────────

    /// <summary>An operator with no dedicated variant, kept with its raw operands.</summary>
    internal sealed record Other(string Name, List<OxOperand> Operands) : OxOperator;

    /// <summary>
    /// True when this operator sets a colour or colour-space parameter.
    /// Per ISO 32000-1:2008 §9.6.5.2 the glyph description of a Type 3 `d1`
    /// glyph is a stencil painted with the current fill colour, so colour
    /// operators inside it are ignored while the `d1` colour lock is in effect.
    /// </summary>
    internal bool IsColorSetting() => this is SetFillRgb
        or SetStrokeRgb
        or SetFillGray
        or SetStrokeGray
        or SetFillCmyk
        or SetStrokeCmyk
        or SetFillColorSpace
        or SetStrokeColorSpace
        or SetFillColor
        or SetStrokeColor
        or SetFillColorN
        or SetStrokeColorN;

    /// <summary>
    /// Validate operand count for a raw operator name against ISO 32000-1:2008
    /// Table A.1. Returns null when valid, otherwise a descriptive message.
    /// Only for strict-mode compliance checks — the parser itself is lenient.
    /// Unknown operators are not validated (lenient by design).
    /// </summary>
    internal static string? ValidateOperandsForRawOperator(string operatorName, IReadOnlyList<OxOperand> operands)
    {
        int n = operands.Count;
        static string? Check(int actual, int want, string message) =>
            actual == want ? null : $"{message}, got {actual}";

        return operatorName switch
        {
            // Path construction — ISO 32000-1 §8.5.2
            "m" => Check(n, 2, "Operator 'm' (moveto) requires 2 operands (x, y)"),
            "l" => Check(n, 2, "Operator 'l' (lineto) requires 2 operands (x, y)"),
            "c" => Check(n, 6, "Operator 'c' (curveto) requires 6 operands (x1, y1, x2, y2, x3, y3)"),
            "v" => Check(n, 4, "Operator 'v' (curveto) requires 4 operands (x2, y2, x3, y3)"),
            "y" => Check(n, 4, "Operator 'y' (curveto) requires 4 operands (x1, y1, x3, y3)"),
            "h" => Check(n, 0, "Operator 'h' (closepath) requires 0 operands"),
            "re" => Check(n, 4, "Operator 're' (rectangle) requires 4 operands (x, y, width, height)"),

            // Text positioning — §9.4.2
            "Td" => Check(n, 2, "Operator 'Td' requires 2 operands (tx, ty)"),
            "TD" => Check(n, 2, "Operator 'TD' requires 2 operands (tx, ty)"),
            "Tm" => Check(n, 6, "Operator 'Tm' requires 6 operands (a, b, c, d, e, f)"),
            "T*" => Check(n, 0, "Operator 'T*' requires 0 operands"),

            // Text showing — §9.4.3
            "Tj" => Check(n, 1, "Operator 'Tj' requires 1 operand (string)"),
            "TJ" => Check(n, 1, "Operator 'TJ' requires 1 operand (array)"),
            "'" => Check(n, 1, "Operator ''' requires 1 operand (string)"),
            "\"" => Check(n, 3, "Operator '\"' requires 3 operands (word_space, char_space, string)"),

            // Text state — §9.3
            "Tc" => Check(n, 1, "Operator 'Tc' requires 1 operand (char_space)"),
            "Tw" => Check(n, 1, "Operator 'Tw' requires 1 operand (word_space)"),
            "Tz" => Check(n, 1, "Operator 'Tz' requires 1 operand (scale)"),
            "TL" => Check(n, 1, "Operator 'TL' requires 1 operand (leading)"),
            "Tf" => Check(n, 2, "Operator 'Tf' requires 2 operands (font, size)"),
            "Tr" => Check(n, 1, "Operator 'Tr' requires 1 operand (render)"),
            "Ts" => Check(n, 1, "Operator 'Ts' requires 1 operand (rise)"),

            // Graphics state
            "q" or "Q" => Check(n, 0, $"Operator '{operatorName}' requires 0 operands"),
            "cm" => Check(n, 6, "Operator 'cm' requires 6 operands (a, b, c, d, e, f)"),

            // Colour — §8.6.8
            "rg" => Check(n, 3, "Operator 'rg' requires 3 operands (r, g, b)"),
            "RG" => Check(n, 3, "Operator 'RG' requires 3 operands (r, g, b)"),
            "g" => Check(n, 1, "Operator 'g' requires 1 operand (gray)"),
            "G" => Check(n, 1, "Operator 'G' requires 1 operand (gray)"),
            "k" => Check(n, 4, "Operator 'k' requires 4 operands (c, m, y, k)"),
            "K" => Check(n, 4, "Operator 'K' requires 4 operands (c, m, y, k)"),

            // Text objects — §9.4
            "BT" or "ET" => Check(n, 0, $"Operator '{operatorName}' requires 0 operands"),

            // XObjects — §8.8
            "Do" => Check(n, 1, "Operator 'Do' requires 1 operand (name)"),

            // Everything else is deliberately unvalidated (lenient by design).
            _ => null,
        };
    }
}
