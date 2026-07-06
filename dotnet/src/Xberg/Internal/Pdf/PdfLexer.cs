// Low-level PDF tokenizer + object parser (ISO 32000-1 §7.2–7.3).
using System.Text;

namespace Xberg.Internal.Pdf;

/// <summary>
/// Parses PDF objects from a byte buffer starting at an arbitrary offset.
/// A resolver delegate is used to resolve indirect stream /Length values.
/// </summary>
public sealed class PdfLexer
{
    private readonly byte[] _buf;
    public int Pos;
    private readonly Func<int, int, PdfObject?>? _resolve;

    public PdfLexer(byte[] buf, int pos = 0, Func<int, int, PdfObject?>? resolve = null)
    {
        _buf = buf; Pos = pos; _resolve = resolve;
    }

    public int Length => _buf.Length;
    public byte[] Buffer => _buf;

    private static bool IsWhite(byte b) => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;
    private static bool IsDelim(byte b) => b == (byte)'(' || b == (byte)')' || b == (byte)'<' || b == (byte)'>'
        || b == (byte)'[' || b == (byte)']' || b == (byte)'{' || b == (byte)'}' || b == (byte)'/' || b == (byte)'%';

    public void SkipWhitespace()
    {
        while (Pos < _buf.Length)
        {
            byte b = _buf[Pos];
            if (b == (byte)'%')
            {
                // Comment to end of line.
                while (Pos < _buf.Length && _buf[Pos] != 10 && _buf[Pos] != 13) Pos++;
            }
            else if (IsWhite(b)) Pos++;
            else break;
        }
    }

    private bool StartsWith(string s)
    {
        if (Pos + s.Length > _buf.Length) return false;
        for (int i = 0; i < s.Length; i++) if (_buf[Pos + i] != (byte)s[i]) return false;
        return true;
    }

    /// <summary>Parse a single object at the current position (skipping leading whitespace).</summary>
    public PdfObject ParseObject()
    {
        SkipWhitespace();
        if (Pos >= _buf.Length) return PdfObject.Null;
        byte b = _buf[Pos];
        switch (b)
        {
            case (byte)'/': return ParseName();
            case (byte)'(': return ParseLiteralString();
            case (byte)'[': return ParseArray();
            case (byte)'<':
                if (Pos + 1 < _buf.Length && _buf[Pos + 1] == (byte)'<') return ParseDictOrStream();
                return ParseHexString();
            case (byte)'t':
                if (StartsWith("true")) { Pos += 4; return new PdfBool(true); }
                break;
            case (byte)'f':
                if (StartsWith("false")) { Pos += 5; return new PdfBool(false); }
                break;
            case (byte)'n':
                if (StartsWith("null")) { Pos += 4; return PdfObject.Null; }
                break;
        }
        if (b == (byte)'+' || b == (byte)'-' || b == (byte)'.' || (b >= (byte)'0' && b <= (byte)'9'))
            return ParseNumberOrRef();
        // Unknown token: consume a keyword-ish run to avoid infinite loop.
        int start = Pos;
        while (Pos < _buf.Length && !IsWhite(_buf[Pos]) && !IsDelim(_buf[Pos])) Pos++;
        if (Pos == start) Pos++;
        return PdfObject.Null;
    }

    private PdfObject ParseNumberOrRef()
    {
        int save = Pos;
        PdfNumber first = ReadNumber();
        if (first.IsInteger && first.Value >= 0)
        {
            // Try "gen R" or "gen obj".
            int afterFirst = Pos;
            SkipWhitespace();
            if (Pos < _buf.Length && _buf[Pos] >= (byte)'0' && _buf[Pos] <= (byte)'9')
            {
                int genStart = Pos;
                PdfNumber gen = ReadNumber();
                if (gen.IsInteger)
                {
                    SkipWhitespace();
                    if (Pos < _buf.Length && _buf[Pos] == (byte)'R'
                        && (Pos + 1 >= _buf.Length || IsWhite(_buf[Pos + 1]) || IsDelim(_buf[Pos + 1])))
                    {
                        Pos++;
                        return new PdfRef((int)first.Value, (int)gen.Value);
                    }
                }
                // Not a ref: rewind to after first number.
                Pos = afterFirst;
                return first;
            }
            Pos = afterFirst;
        }
        return first;
    }

    private PdfNumber ReadNumber()
    {
        int start = Pos;
        bool isInt = true;
        if (Pos < _buf.Length && (_buf[Pos] == (byte)'+' || _buf[Pos] == (byte)'-')) Pos++;
        while (Pos < _buf.Length)
        {
            byte b = _buf[Pos];
            if (b >= (byte)'0' && b <= (byte)'9') Pos++;
            else if (b == (byte)'.') { isInt = false; Pos++; }
            else if (b == (byte)'-' || b == (byte)'+') Pos++; // tolerate embedded signs
            else if (b == (byte)'e' || b == (byte)'E') { isInt = false; Pos++; }
            else break;
        }
        string s = Encoding.ASCII.GetString(_buf, start, Pos - start);
        double val = ParseLenientDouble(s);
        return new PdfNumber(val, isInt);
    }

    internal static double ParseLenientDouble(string s)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        // Handle malformed like "--", "." etc.
        var sb = new StringBuilder();
        bool dot = false, sign = false;
        foreach (char c in s)
        {
            if (c >= '0' && c <= '9') sb.Append(c);
            else if (c == '.' && !dot) { dot = true; sb.Append(c); }
            else if ((c == '-' || c == '+') && sb.Length == 0 && !sign) { sign = true; if (c == '-') sb.Append(c); }
        }
        return double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0.0;
    }

    private PdfName ParseName()
    {
        Pos++; // consume '/'
        var sb = new StringBuilder();
        while (Pos < _buf.Length)
        {
            byte b = _buf[Pos];
            if (IsWhite(b) || IsDelim(b)) break;
            if (b == (byte)'#' && Pos + 2 < _buf.Length && IsHex(_buf[Pos + 1]) && IsHex(_buf[Pos + 2]))
            {
                int hi = HexVal(_buf[Pos + 1]); int lo = HexVal(_buf[Pos + 2]);
                sb.Append((char)((hi << 4) | lo));
                Pos += 3;
            }
            else { sb.Append((char)b); Pos++; }
        }
        return new PdfName(sb.ToString());
    }

    private PdfString ParseLiteralString()
    {
        Pos++; // '('
        var bytes = new List<byte>();
        int depth = 1;
        while (Pos < _buf.Length)
        {
            byte b = _buf[Pos++];
            if (b == (byte)'\\')
            {
                if (Pos >= _buf.Length) break;
                byte e = _buf[Pos++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'(': bytes.Add((byte)'('); break;
                    case (byte)')': bytes.Add((byte)')'); break;
                    case (byte)'\\': bytes.Add((byte)'\\'); break;
                    case 13: // line continuation \CR or \CRLF
                        if (Pos < _buf.Length && _buf[Pos] == 10) Pos++;
                        break;
                    case 10: break; // line continuation \LF
                    default:
                        if (e >= (byte)'0' && e <= (byte)'7')
                        {
                            int val = e - (byte)'0';
                            for (int k = 0; k < 2 && Pos < _buf.Length && _buf[Pos] >= (byte)'0' && _buf[Pos] <= (byte)'7'; k++)
                                val = (val << 3) | (_buf[Pos++] - (byte)'0');
                            bytes.Add((byte)(val & 0xFF));
                        }
                        else bytes.Add(e);
                        break;
                }
            }
            else if (b == (byte)'(') { depth++; bytes.Add(b); }
            else if (b == (byte)')') { depth--; if (depth == 0) break; bytes.Add(b); }
            else bytes.Add(b);
        }
        return new PdfString(bytes.ToArray());
    }

    private PdfString ParseHexString()
    {
        Pos++; // '<'
        var bytes = new List<byte>();
        int hi = -1;
        while (Pos < _buf.Length)
        {
            byte b = _buf[Pos++];
            if (b == (byte)'>') break;
            if (!IsHex(b)) continue;
            int v = HexVal(b);
            if (hi < 0) hi = v;
            else { bytes.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) bytes.Add((byte)(hi << 4));
        return new PdfString(bytes.ToArray());
    }

    private PdfArray ParseArray()
    {
        Pos++; // '['
        var arr = new PdfArray();
        while (true)
        {
            SkipWhitespace();
            if (Pos >= _buf.Length) break;
            if (_buf[Pos] == (byte)']') { Pos++; break; }
            int before = Pos;
            arr.Items.Add(ParseObject());
            if (Pos == before) { Pos++; if (Pos > _buf.Length) break; }
        }
        return arr;
    }

    private PdfObject ParseDictOrStream()
    {
        Pos += 2; // '<<'
        var dict = new PdfDict();
        while (true)
        {
            SkipWhitespace();
            if (Pos >= _buf.Length) break;
            if (_buf[Pos] == (byte)'>' && Pos + 1 < _buf.Length && _buf[Pos + 1] == (byte)'>') { Pos += 2; break; }
            if (_buf[Pos] != (byte)'/')
            {
                // Malformed key; skip a token to avoid infinite loop.
                int before = Pos;
                ParseObject();
                if (Pos == before) Pos++;
                continue;
            }
            string key = ParseName().Value;
            PdfObject val = ParseObject();
            dict.Map[key] = val;
        }

        // Check for "stream".
        int save = Pos;
        SkipWhitespace();
        if (StartsWith("stream"))
        {
            Pos += 6;
            // After "stream" keyword: CRLF or LF.
            if (Pos < _buf.Length && _buf[Pos] == 13) Pos++;
            if (Pos < _buf.Length && _buf[Pos] == 10) Pos++;
            int dataStart = Pos;

            int? len = ResolveLength(dict.Get("Length"));
            int dataEnd;
            if (len is int L && L >= 0 && dataStart + L <= _buf.Length)
            {
                dataEnd = dataStart + L;
                // Verify endstream follows within a small window; else fall back to scan.
                int probe = dataEnd;
                int white = 0;
                while (probe < _buf.Length && IsWhite(_buf[probe]) && white < 4) { probe++; white++; }
                if (!(probe + 9 <= _buf.Length && MatchAt(probe, "endstream")))
                    dataEnd = ScanEndstream(dataStart);
            }
            else
            {
                dataEnd = ScanEndstream(dataStart);
            }
            if (dataEnd < dataStart) dataEnd = dataStart;
            var raw = new byte[dataEnd - dataStart];
            Array.Copy(_buf, dataStart, raw, 0, raw.Length);
            Pos = dataEnd;
            // Skip trailing whitespace + endstream.
            SkipWhitespace();
            if (StartsWith("endstream")) Pos += 9;
            return new PdfStream(dict, raw);
        }
        Pos = save;
        return dict;
    }

    private int ScanEndstream(int from)
    {
        // Find "endstream", trimming a single preceding EOL.
        int i = from;
        while (i + 9 <= _buf.Length)
        {
            if (_buf[i] == (byte)'e' && MatchAt(i, "endstream"))
            {
                int end = i;
                if (end > from && _buf[end - 1] == 10) end--;
                if (end > from && _buf[end - 1] == 13) end--;
                return end;
            }
            i++;
        }
        return _buf.Length;
    }

    private bool MatchAt(int at, string s)
    {
        if (at + s.Length > _buf.Length) return false;
        for (int i = 0; i < s.Length; i++) if (_buf[at + i] != (byte)s[i]) return false;
        return true;
    }

    private int? ResolveLength(PdfObject? lenObj)
    {
        if (lenObj is PdfNumber n) return (int)n.Value;
        if (lenObj is PdfRef r && _resolve != null)
        {
            var resolved = _resolve(r.Number, r.Generation);
            if (resolved is PdfNumber rn) return (int)rn.Value;
        }
        return null;
    }

    /// <summary>Parse "num gen obj &lt;object&gt; endobj". Assumes Pos at object start.</summary>
    public PdfObject ParseIndirectObject()
    {
        SkipWhitespace();
        ReadNumber(); // obj number
        SkipWhitespace();
        ReadNumber(); // gen
        SkipWhitespace();
        if (StartsWith("obj")) Pos += 3;
        var o = ParseObject();
        return o;
    }

    // Reads the next bare token (keyword) for content streams / xref parsing.
    public string? ReadToken()
    {
        SkipWhitespace();
        if (Pos >= _buf.Length) return null;
        int start = Pos;
        while (Pos < _buf.Length && !IsWhite(_buf[Pos]) && !IsDelim(_buf[Pos])) Pos++;
        if (Pos == start) { Pos++; return ((char)_buf[start]).ToString(); }
        return Encoding.ASCII.GetString(_buf, start, Pos - start);
    }

    private static bool IsHex(byte b) => (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f') || (b >= (byte)'A' && b <= (byte)'F');
    private static int HexVal(byte b) => b <= (byte)'9' ? b - (byte)'0' : (b | 0x20) - (byte)'a' + 10;
}
