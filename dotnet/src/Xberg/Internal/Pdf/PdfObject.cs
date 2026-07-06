// Pure-managed PDF object model. Part of the C# port of the pdf_oxide/lopdf backend
// used by crates/xberg/src/pdf/**. No native dependencies.
using System.Globalization;

namespace Xberg.Internal.Pdf;

/// <summary>Base type for all PDF objects (ISO 32000-1 §7.3).</summary>
public abstract class PdfObject
{
    public static readonly PdfNull Null = new();
}

public sealed class PdfNull : PdfObject { }

public sealed class PdfBool : PdfObject
{
    public bool Value;
    public PdfBool(bool v) => Value = v;
}

public sealed class PdfNumber : PdfObject
{
    public double Value;
    public bool IsInteger;
    public PdfNumber(double v, bool isInt) { Value = v; IsInteger = isInt; }
    public long AsLong => (long)Value;
    public int AsInt => (int)Value;
}

/// <summary>A PDF string stored as raw bytes (may be literal or hex).</summary>
public sealed class PdfString : PdfObject
{
    public byte[] Bytes;
    public PdfString(byte[] b) => Bytes = b;
}

public sealed class PdfName : PdfObject
{
    public string Value;
    public PdfName(string v) => Value = v;
}

public sealed class PdfArray : PdfObject
{
    public List<PdfObject> Items = new();
}

public sealed class PdfDict : PdfObject
{
    public Dictionary<string, PdfObject> Map = new(StringComparer.Ordinal);
    public PdfObject? Get(string key) => Map.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => Map.ContainsKey(key);
}

/// <summary>A stream object: a dictionary plus its (still-encoded) raw byte payload.</summary>
public sealed class PdfStream : PdfObject
{
    public PdfDict Dict;
    public byte[] RawData;
    public PdfStream(PdfDict dict, byte[] raw) { Dict = dict; RawData = raw; }
}

/// <summary>An indirect reference "n g R".</summary>
public sealed class PdfRef : PdfObject
{
    public int Number;
    public int Generation;
    public PdfRef(int num, int gen) { Number = num; Generation = gen; }
    public override int GetHashCode() => (Number * 397) ^ Generation;
    public override bool Equals(object? obj) => obj is PdfRef r && r.Number == Number && r.Generation == Generation;
}

public static class PdfObjectExtensions
{
    public static PdfDict? AsDict(this PdfObject? o) => o switch
    {
        PdfDict d => d,
        PdfStream s => s.Dict,
        _ => null,
    };

    public static double? AsNumber(this PdfObject? o) => o is PdfNumber n ? n.Value : (double?)null;
    public static long? AsLong(this PdfObject? o) => o is PdfNumber n ? (long)n.Value : (long?)null;
    public static string? AsName(this PdfObject? o) => o is PdfName n ? n.Value : null;
    public static bool? AsBool(this PdfObject? o) => o is PdfBool b ? b.Value : (bool?)null;
    public static PdfArray? AsArray(this PdfObject? o) => o as PdfArray;
    public static byte[]? AsStringBytes(this PdfObject? o) => o is PdfString s ? s.Bytes : null;
}
