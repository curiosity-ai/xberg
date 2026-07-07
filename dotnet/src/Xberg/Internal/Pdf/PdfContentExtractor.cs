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

    public PdfContentExtractor(PdfDocument doc, long deadlineTicks) { _doc = doc; _deadline = deadlineTicks; }

    public List<TextSpan> Extract(byte[] content, PdfDict? resources)
    {
        Run(content, resources, 0);
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

    private Dictionary<string, PdfFont> LoadFonts(PdfDict? resources)
    {
        var map = new Dictionary<string, PdfFont>(StringComparer.Ordinal);
        var fontsDict = _doc.Resolve(resources?.Get("Font")).AsDict();
        if (fontsDict == null) return map;
        foreach (var kv in fontsDict.Map)
        {
            var fd = _doc.Resolve(kv.Value).AsDict();
            if (fd != null)
            {
                try { map[kv.Key] = PdfFont.Load(fd, _doc); } catch { }
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
            case "Q": if (_stack.Count > 0) _gs = _stack.Pop(); break;
            case "cm":
                if (ops.Count >= 6)
                    _gs.Ctm = new Matrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)).Multiply(_gs.Ctm);
                break;
            case "BT": _tm = Matrix.Identity; _tlm = Matrix.Identity; break;
            case "ET": break;
            case "Td":
                if (ops.Count >= 2) { _tlm = new Matrix(1, 0, 0, 1, Num(ops, 0), Num(ops, 1)).Multiply(_tlm); _tm = _tlm; }
                break;
            case "TD":
                if (ops.Count >= 2)
                {
                    _gs.Leading = -Num(ops, 1);
                    _tlm = new Matrix(1, 0, 0, 1, Num(ops, 0), Num(ops, 1)).Multiply(_tlm); _tm = _tlm;
                }
                break;
            case "Tm":
                if (ops.Count >= 6) { _tlm = new Matrix(Num(ops, 0), Num(ops, 1), Num(ops, 2), Num(ops, 3), Num(ops, 4), Num(ops, 5)); _tm = _tlm; }
                break;
            case "T*":
                _tlm = new Matrix(1, 0, 0, 1, 0, -_gs.Leading).Multiply(_tlm); _tm = _tlm;
                break;
            case "Tc": _gs.CharSpace = Num(ops, 0); break;
            case "Tw": _gs.WordSpace = Num(ops, 0); break;
            case "Tz": _gs.Hscale = Num(ops, 0); break;
            case "TL": _gs.Leading = Num(ops, 0); break;
            case "Ts": _gs.Rise = Num(ops, 0); break;
            case "Tf":
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
                _tlm = new Matrix(1, 0, 0, 1, 0, -_gs.Leading).Multiply(_tlm); _tm = _tlm;
                if (ops.Count >= 1 && ops[^1] is PdfString s2) ShowText(s2.Bytes);
                break;
            case "\"":
                if (ops.Count >= 3)
                {
                    _gs.WordSpace = Num(ops, 0);
                    _gs.CharSpace = Num(ops, 1);
                    _tlm = new Matrix(1, 0, 0, 1, 0, -_gs.Leading).Multiply(_tlm); _tm = _tlm;
                    if (ops[^1] is PdfString s3) ShowText(s3.Bytes);
                }
                break;
            case "TJ":
                if (ops.Count >= 1 && ops[^1] is PdfArray arr) ShowTextArray(arr);
                break;
            case "Do":
                if (ops.Count >= 1 && ops[0] is PdfName xn) DoXObject(xn.Value, xobjects, depth);
                break;
        }
    }

    private void ShowTextArray(PdfArray arr)
    {
        foreach (var it in arr.Items)
        {
            if (it is PdfString s) ShowText(s.Bytes);
            else if (it is PdfNumber n)
            {
                // TJ adjustment: subtract n/1000 * fontSize * Tz from text position.
                double adj = -n.Value / 1000.0 * _gs.FontSize * (_gs.Hscale / 100.0);
                _tm = new Matrix(1, 0, 0, 1, adj, 0).Multiply(_tm);
            }
        }
    }

    private void ShowText(byte[] bytes)
    {
        var font = _gs.Font;
        double fontSize = _gs.FontSize;
        double hs = _gs.Hscale / 100.0;
        double fmA = font?.FontMatrixA ?? 0.001;

        var sb = new StringBuilder();
        // Start position of the span.
        var startCombined = _tm.Multiply(_gs.Ctm);
        double startX = startCombined.E;
        double startY = startCombined.F + _gs.Rise * Math.Sqrt(startCombined.B * startCombined.B + startCombined.D * startCombined.D) * Math.Sign(1);
        double effSize = fontSize * Math.Sqrt(startCombined.D * startCombined.D + startCombined.B * startCombined.B);
        bool any = false;
        double lastEndX = startX;
        double baselineY = startCombined.F;

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
                sb.Append(ch);
                any = true;
            }

            // Advance text matrix.
            _tm = new Matrix(1, 0, 0, 1, tx, 0).Multiply(_tm);
        }
        var endCombined = _tm.Multiply(_gs.Ctm);
        double endX = endCombined.E;

        if (any)
        {
            _spans.Add(new TextSpan
            {
                Text = sb.ToString(),
                X = startX,
                Y = baselineY,
                Width = endX - startX,
                Height = effSize,
                FontSize = (float)effSize,
                IsBold = font?.IsBold ?? false,
                IsItalic = font?.IsItalic ?? false,
                IsMonospace = font?.IsMonospace ?? false,
            });
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
