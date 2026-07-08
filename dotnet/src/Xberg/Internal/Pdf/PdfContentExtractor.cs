// Content-stream interpreter: tracks the graphics/text state (ISO 32000-1 §8–9),
// executes text-showing operators (Tj/TJ/'/"), and emits positioned text spans.
// Approximates pdf_oxide's TextExtractor span output.
using System.Text;

namespace Xberg.Internal.Pdf;

public struct Matrix
{
    public double A, B, C, D, E, F;
    public Matrix(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public (double x, double y) Transform(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    // Result applies `this` first, then `m`  (result = this · m).
    public Matrix Multiply(in Matrix m) => new(
        m.A * A + m.C * B,
        m.B * A + m.D * B,
        m.A * C + m.C * D,
        m.B * C + m.D * D,
        m.A * E + m.C * F + m.E,
        m.B * E + m.D * F + m.F);
}

public sealed class TextSpan
{
    public string Text = "";
    public double X, Y, Width, Height;
    public double FontSize;
    public bool IsBold;
    public bool IsItalic;
    public bool IsMonospace;

    // Geometry accessors mirroring pdf_oxide Rect (PDF coords: Y grows up).
    public double Left => X;
    public double Right => X + Width;
    public double Top => Y;      // pdf_oxide Rect::top() == y (lower edge); larger Y = higher on page
    public double Bottom => Y + Height;
}

public sealed class PdfContentExtractor
{
    private readonly PdfDocument _doc;
    private readonly List<TextSpan> _spans = new();

    private sealed class GState
    {
        public Matrix Ctm = Matrix.Identity;
        public double CharSpace, WordSpace;
        public double Hscale = 100.0;
        public double Leading;
        public double FontSize;
        public double Rise;
        public PdfFont? Font;
        public GState Clone() => (GState)MemberwiseClone();
    }

    private GState _gs = new();
    private readonly Stack<GState> _stack = new();
    private Matrix _tm = Matrix.Identity;
    private Matrix _tlm = Matrix.Identity;
    private readonly long _deadline;

    // Show-op span buffer, mirroring pdf_oxide's TjBuffer (extractors/text.rs).
    // A whole TJ array — and consecutive Tj operators — accumulate into ONE
    // span; only large negative TJ offsets (< SpaceInsertionThreshold) split
    // it. Emitting one span per show-string scrambles kerned runs once spans
    // are sorted by position (senate-expenditures fixture).
    private sealed class TjBuffer
    {
        public StringBuilder Text = new();
        public double StartX, StartY;          // user-space origin at buffer start
        public double AccumWidth;              // text-space width (incl. Tz)
        public double UserHScale;              // sqrt(a²+c²) of combined matrix
        public double EffFontSize;             // font_size * sqrt(b²+d²)
        public bool IsBold, IsItalic, IsMonospace;
        public bool HasRtl;
    }

    private TjBuffer? _buf;

    // pdf_oxide TextExtractionConfig::default().space_insertion_threshold.
    private const double SpaceInsertionThreshold = -120.0;

    public PdfContentExtractor(PdfDocument doc, long deadlineTicks) { _doc = doc; _deadline = deadlineTicks; }

    public List<TextSpan> Extract(byte[] content, PdfDict? resources)
    {
        Run(content, resources, 0);
        FlushBuffer();
        return _spans;
    }

    private void Run(byte[] content, PdfDict? resources, int depth)
    {
        if (depth > 12) return;
        var fonts = LoadFonts(resources);
        var xobjects = _doc.Resolve(resources?.Get("XObject")).AsDict();

        var lex = new PdfLexer(content, 0, null);
        var operands = new List<PdfObject>();
        while (lex.Pos < lex.Length)
        {
            if (DateTime.UtcNow.Ticks > _deadline) return;
            lex.SkipWhitespace();
            if (lex.Pos >= lex.Length) break;
            byte b = lex.Buffer[lex.Pos];
            if (b == (byte)'<' || b == (byte)'(' || b == (byte)'[' || b == (byte)'/' ||
                b == (byte)'+' || b == (byte)'-' || b == (byte)'.' || (b >= (byte)'0' && b <= (byte)'9'))
            {
                int before = lex.Pos;
                operands.Add(lex.ParseObject());
                if (lex.Pos == before) lex.Pos++;
                if (operands.Count > 64) operands.RemoveRange(0, operands.Count - 32);
                continue;
            }
            string? op = lex.ReadToken();
            if (op == null) break;
            if (op == "BI") { SkipInlineImage(lex); operands.Clear(); continue; }
            try { Execute(op, operands, fonts, xobjects, depth); }
            catch { /* keep going */ }
            operands.Clear();
        }
    }

    // Per-document font cache: resolved font dicts are reference-cached by
    // PdfDocument, so the same PdfDict instance recurs across pages. Parsing
    // a font (ToUnicode CMap, widths, encoding) is expensive; without this
    // cache large books re-parse every font on every page (bayesian/intel
    // fixtures tripped the 25s guard).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDocument, Dictionary<PdfDict, PdfFont>> FontCaches = new();

    private Dictionary<string, PdfFont> LoadFonts(PdfDict? resources)
    {
        var map = new Dictionary<string, PdfFont>(StringComparer.Ordinal);
        var fontsDict = _doc.Resolve(resources?.Get("Font")).AsDict();
        if (fontsDict == null) return map;
        var cache = FontCaches.GetOrCreateValue(_doc);
        foreach (var kv in fontsDict.Map)
        {
            var fd = _doc.Resolve(kv.Value).AsDict();
            if (fd != null)
            {
                if (cache.TryGetValue(fd, out var cached)) { map[kv.Key] = cached; continue; }
                try
                {
                    var font = PdfFont.Load(fd, _doc);
                    cache[fd] = font;
                    map[kv.Key] = font;
                }
                catch { }
            }
        }
        return map;
    }

    private static double Num(List<PdfObject> ops, int i) => i < ops.Count ? (ops[i].AsNumber() ?? 0) : 0;

    private void Execute(string op, List<PdfObject> ops, Dictionary<string, PdfFont> fonts, PdfDict? xobjects, int depth)
    {
        switch (op)
        {
            case "q": _stack.Push(_gs.Clone()); break;
            case "Q": FlushBuffer(); if (_stack.Count > 0) _gs = _stack.Pop(); break;
            case "cm":
                if (ops.Count >= 6)
                {
                    FlushBuffer();
                    _gs.Ctm = new Matrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)).Multiply(_gs.Ctm);
                }
                break;
            case "BT": FlushBuffer(); _tm = Matrix.Identity; _tlm = Matrix.Identity; break;
            case "ET": FlushBuffer(); break;
            case "Td":
                if (ops.Count >= 2) { FlushBuffer(); _tlm = new Matrix(1, 0, 0, 1, Num(ops, 0), Num(ops, 1)).Multiply(_tlm); _tm = _tlm; }
                break;
            case "TD":
                if (ops.Count >= 2)
                {
                    FlushBuffer();
                    _gs.Leading = -Num(ops, 1);
                    _tlm = new Matrix(1, 0, 0, 1, Num(ops, 0), Num(ops, 1)).Multiply(_tlm); _tm = _tlm;
                }
                break;
            case "Tm":
                if (ops.Count >= 6) { FlushBuffer(); _tlm = new Matrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)); _tm = _tlm; }
                break;
            case "T*":
                FlushBuffer();
                _tlm = new Matrix(1, 0, 0, 1, 0, -_gs.Leading).Multiply(_tlm); _tm = _tlm;
                break;
            case "Tc": _gs.CharSpace = Num(ops, 0); break;
            case "Tw": _gs.WordSpace = Num(ops, 0); break;
            case "Tz": _gs.Hscale = Num(ops, 0); break;
            case "TL": _gs.Leading = Num(ops, 0); break;
            case "Ts": _gs.Rise = Num(ops, 0); break;
            case "Tf":
                FlushBuffer();
                if (ops.Count >= 2 && ops[0] is PdfName fn)
                {
                    fonts.TryGetValue(fn.Value, out var font);
                    _gs.Font = font;
                    _gs.FontSize = Num(ops, 1);
                }
                break;
            case "Tj":
                if (ops.Count >= 1 && ops[^1] is PdfString s) ShowText(s.Bytes);
                break;
            case "'":
                FlushBuffer();
                _tlm = new Matrix(1, 0, 0, 1, 0, -_gs.Leading).Multiply(_tlm); _tm = _tlm;
                if (ops.Count >= 1 && ops[^1] is PdfString s2) ShowText(s2.Bytes);
                break;
            case "\"":
                if (ops.Count >= 3)
                {
                    FlushBuffer();
                    _gs.WordSpace = Num(ops, 0);
                    _gs.CharSpace = Num(ops, 1);
                    _tlm = new Matrix(1, 0, 0, 1, 0, -_gs.Leading).Multiply(_tlm); _tm = _tlm;
                    if (ops[^1] is PdfString s3) ShowText(s3.Bytes);
                }
                break;
            case "TJ":
                FlushBuffer();
                if (ops.Count >= 1 && ops[^1] is PdfArray arr) ShowTextArray(arr);
                break;
            case "Do":
                FlushBuffer();
                if (ops.Count >= 1 && ops[0] is PdfName xn) DoXObject(xn.Value, xobjects, depth);
                break;
        }
    }

    // TJ array (pdf_oxide process_tj_array_tiebreaker): accumulate all strings
    // into one span; only offsets below SpaceInsertionThreshold split the span
    // (flush + synthetic space span, mirroring insert_space_as_span).
    private void ShowTextArray(PdfArray arr)
    {
        for (int i = 0; i < arr.Items.Count; i++)
        {
            var it = arr.Items[i];
            if (it is PdfString s) ShowText(s.Bytes);
            else if (it is PdfNumber n)
            {
                if (n.Value < SpaceInsertionThreshold)
                {
                    bool bufEndsWithSpace = _buf != null && _buf.Text.Length > 0
                        && char.IsWhiteSpace(_buf.Text[^1]);
                    FlushBuffer();
                    bool nextStartsWithSpace = i + 1 < arr.Items.Count
                        && arr.Items[i + 1] is PdfString ns && ns.Bytes.Length > 0
                        && (ns.Bytes[0] == 0x20 || ns.Bytes[0] == 0x09 || ns.Bytes[0] == 0x0A || ns.Bytes[0] == 0x0D);
                    if (!bufEndsWithSpace && !nextStartsWithSpace) InsertSpaceSpan();
                }
                // TJ adjustment: subtract n/1000 * fontSize * Tz from text position.
                double adj = -n.Value / 1000.0 * _gs.FontSize * (_gs.Hscale / 100.0);
                _tm = new Matrix(1, 0, 0, 1, adj, 0).Multiply(_tm);
            }
        }
    }

    // Synthetic space span for a large TJ offset (pdf_oxide insert_space_as_span).
    private void InsertSpaceSpan()
    {
        var combined = _tm.Multiply(_gs.Ctm);
        double effSize = _gs.FontSize * Math.Sqrt(combined.D * combined.D + combined.B * combined.B);
        double spaceAdvance = (250.0 * _gs.FontSize / 1000.0 + _gs.WordSpace) * (_gs.Hscale / 100.0);
        _spans.Add(new TextSpan
        {
            Text = " ",
            X = combined.E,
            Y = combined.F,
            Width = spaceAdvance,
            Height = effSize,
            FontSize = effSize,
            IsBold = false,
            IsItalic = _gs.Font?.IsItalic ?? false,
            IsMonospace = false,
        });
    }

    private void StartBuffer()
    {
        var combined = _tm.Multiply(_gs.Ctm);
        _buf = new TjBuffer
        {
            StartX = combined.E,
            StartY = combined.F,
            EffFontSize = _gs.FontSize * Math.Sqrt(combined.D * combined.D + combined.B * combined.B),
            UserHScale = Math.Sqrt(combined.A * combined.A + combined.C * combined.C),
            IsBold = _gs.Font?.IsBold ?? false,
            IsItalic = _gs.Font?.IsItalic ?? false,
            IsMonospace = _gs.Font?.IsMonospace ?? false,
        };
    }

    private void FlushBuffer()
    {
        var b = _buf;
        _buf = null;
        if (b == null || b.Text.Length == 0) return;
        string text = b.Text.ToString();
        // RTL correction (pdf_oxide flush_tj_buffer): text within a buffer is
        // in visual LTR draw order; when it contains RTL characters and the
        // buffer advanced left-to-right, reverse to logical order.
        if (text.Length > 1 && b.HasRtl && b.AccumWidth > 0.0)
            text = PdfBidi.ReverseRtlKeepNumbers(text);
        _spans.Add(new TextSpan
        {
            Text = text,
            X = b.StartX,
            Y = b.StartY,
            Width = b.AccumWidth * b.UserHScale,
            Height = b.EffFontSize,
            FontSize = b.EffFontSize,
            IsBold = b.IsBold,
            IsItalic = b.IsItalic,
            IsMonospace = b.IsMonospace,
        });
    }

    private void ShowText(byte[] bytes)
    {
        var font = _gs.Font;
        double fontSize = _gs.FontSize;
        double hs = _gs.Hscale / 100.0;
        double fmA = font?.FontMatrixA ?? 0.001;

        if (_buf == null) StartBuffer();
        var buf = _buf!;

        var codes = font != null ? font.DecodeCodes(bytes) : DefaultCodes(bytes);
        foreach (int code in codes)
        {
            string u = font != null ? font.CharToUnicode(code) : ((code >= 32 && code < 127) ? ((char)code).ToString() : "");
            double w0 = (font?.GlyphWidth(code) ?? 500.0) * fmA; // text-space width (em fraction)
            double tx = (w0 * fontSize + _gs.CharSpace + (code == 32 ? _gs.WordSpace : 0)) * hs;

            // Filter control chars like pdf_oxide (skip NUL and C0 except tab/newline/cr).
            foreach (var ch in u)
            {
                if (ch == '\0' || (char.IsControl(ch) && ch != '\t' && ch != '\n' && ch != '\r')) continue;
                buf.Text.Append(ch);
                if (!buf.HasRtl && PdfBidi.IsRtlText(ch)) buf.HasRtl = true;
            }

            // Advance text matrix.
            _tm = new Matrix(1, 0, 0, 1, tx, 0).Multiply(_tm);
            buf.AccumWidth += tx;
        }
    }

    private static IEnumerable<int> DefaultCodes(byte[] bytes)
    {
        foreach (var b in bytes) yield return b;
    }

    private void DoXObject(string name, PdfDict? xobjects, int depth)
    {
        var xobj = _doc.Resolve(xobjects?.Get(name));
        if (xobj is not PdfStream st) return;
        if (_doc.Resolve(st.Dict.Get("Subtype")).AsName() != "Form") return;
        // Save state, apply form Matrix, run form content with its resources.
        _stack.Push(_gs.Clone());
        var savedTm = _tm; var savedTlm = _tlm;
        if (_doc.Resolve(st.Dict.Get("Matrix")).AsArray() is PdfArray fm && fm.Items.Count >= 6)
        {
            var m = new Matrix(fm.Items[0].AsNumber() ?? 1, fm.Items[1].AsNumber() ?? 0,
                fm.Items[2].AsNumber() ?? 0, fm.Items[3].AsNumber() ?? 1,
                fm.Items[4].AsNumber() ?? 0, fm.Items[5].AsNumber() ?? 0);
            _gs.Ctm = m.Multiply(_gs.Ctm);
        }
        var formRes = _doc.Resolve(st.Dict.Get("Resources")).AsDict();
        try { Run(_doc.DecodeStream(st), formRes, depth + 1); } catch { }
        FlushBuffer();
        if (_stack.Count > 0) _gs = _stack.Pop();
        _tm = savedTm; _tlm = savedTlm;
    }

    private void SkipInlineImage(PdfLexer lex)
    {
        // Skip until "EI" delimiter.
        while (lex.Pos + 1 < lex.Length)
        {
            if (lex.Buffer[lex.Pos] == (byte)'E' && lex.Buffer[lex.Pos + 1] == (byte)'I'
                && (lex.Pos + 2 >= lex.Length || lex.Buffer[lex.Pos + 2] <= 32))
            {
                lex.Pos += 2;
                return;
            }
            lex.Pos++;
        }
    }
}
