// Ported from pdf_oxide `extractors::page_labels`, as xberg reaches it through
// `pdf::oxide::metadata::extract_page_labels_all`.
//
// /PageLabels (ISO 32000-1 §12.4.2) is a number tree keyed by zero-based page
// index: roman-numeral front matter followed by arabic body pages, or
// per-section prefixed numbering.
using System;
using System.Collections.Generic;
using System.Text;

namespace Xberg.Internal.Pdf;

internal enum PageLabelStyle { Decimal, RomanUpper, RomanLower, AlphaUpper, AlphaLower, None }

internal sealed class PageLabelRange
{
    /// <summary>Zero-based page index where this labelling range begins.</summary>
    public int StartPage;
    public PageLabelStyle Style = PageLabelStyle.Decimal;
    public string? Prefix;
    /// <summary>Numeric value of the first page in the range.</summary>
    public uint StartValue = 1;

    public string FormatLabel(int pageIndex)
    {
        uint number = StartValue + (uint)(pageIndex - StartPage);
        string numberStr = Style switch
        {
            PageLabelStyle.Decimal => number.ToString(),
            PageLabelStyle.RomanUpper => ToRoman(number, true),
            PageLabelStyle.RomanLower => ToRoman(number, false),
            PageLabelStyle.AlphaUpper => ToAlpha(number, true),
            PageLabelStyle.AlphaLower => ToAlpha(number, false),
            _ => "",
        };
        return Prefix is null ? numberStr : Prefix + numberStr;
    }

    private static readonly (uint Value, string Numeral)[] RomanNumerals =
    {
        (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
        (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"),
    };

    private static string ToRoman(uint n, bool uppercase)
    {
        if (n == 0) return "";
        var sb = new StringBuilder();
        foreach (var (value, numeral) in RomanNumerals)
            while (n >= value) { sb.Append(numeral); n -= value; }
        return uppercase ? sb.ToString().ToUpperInvariant() : sb.ToString();
    }

    /// <summary>1=A, 2=B, … 26=Z, 27=AA, 28=AB, …</summary>
    private static string ToAlpha(uint n, bool uppercase)
    {
        if (n == 0) return "";
        var sb = new StringBuilder();
        char b = uppercase ? 'A' : 'a';
        while (n > 0)
        {
            n -= 1;
            sb.Insert(0, (char)(b + (int)(n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }
}

internal static class PdfPageLabels
{
    private const int MaxTreeDepth = 50;

    /// <summary>
    /// One display label per page, or null when the document defines no /PageLabels —
    /// in which case every page uses its plain 1-based number, which callers already have.
    /// </summary>
    public static List<string>? ExtractAll(PdfDocument doc)
    {
        var catalog = doc.Catalog;
        if (catalog?.Get("PageLabels") is not { } pageLabels) return null;

        var ranges = new List<PageLabelRange>();
        ParseNumberTree(doc, pageLabels, ranges, 0);
        if (ranges.Count == 0) return null;
        ranges.Sort((a, b) => a.StartPage.CompareTo(b.StartPage));

        int pageCount = doc.PageCount;
        var labels = new List<string>(pageCount);
        for (int i = 0; i < pageCount; i++) labels.Add(LabelFor(ranges, i));
        return labels;
    }

    /// <summary>The last range starting at or before this page decides its label.</summary>
    private static string LabelFor(List<PageLabelRange> ranges, int pageIndex)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
            if (ranges[i].StartPage <= pageIndex) return ranges[i].FormatLabel(pageIndex);
        return (pageIndex + 1).ToString();
    }

    private static void ParseNumberTree(PdfDocument doc, PdfObject treeObj, List<PageLabelRange> ranges, int depth)
    {
        if (depth > MaxTreeDepth) return;
        var tree = doc.Resolve(treeObj).AsDict();
        if (tree is null) return;

        // A leaf node's /Nums is [pageIndex, labelDict, pageIndex, labelDict, …].
        if (doc.Resolve(tree.Get("Nums")).AsArray() is { } nums)
            for (int i = 0; i + 1 < nums.Items.Count; i += 2)
            {
                if (nums.Items[i].AsLong() is not { } pageIndex || pageIndex < 0) continue;
                var range = ParseLabelDict(doc, nums.Items[i + 1]);
                if (range is null) continue;
                range.StartPage = (int)pageIndex;
                ranges.Add(range);
            }

        if (doc.Resolve(tree.Get("Kids")).AsArray() is { } kids)
            foreach (var kid in kids.Items) ParseNumberTree(doc, kid, ranges, depth + 1);
    }

    private static PageLabelRange? ParseLabelDict(PdfDocument doc, PdfObject dictObj)
    {
        var dict = doc.Resolve(dictObj).AsDict();
        if (dict is null) return null;

        var range = new PageLabelRange();

        // A range with no /S is prefix-only: its pages carry the prefix and no number.
        if (dict.Get("S") is { } styleObj)
        {
            range.Style = doc.Resolve(styleObj).AsName() switch
            {
                "D" => PageLabelStyle.Decimal,
                "R" => PageLabelStyle.RomanUpper,
                "r" => PageLabelStyle.RomanLower,
                "A" => PageLabelStyle.AlphaUpper,
                "a" => PageLabelStyle.AlphaLower,
                _ => PageLabelStyle.None,
            };
        }
        else range.Style = PageLabelStyle.None;

        if (doc.Resolve(dict.Get("P")).AsStringBytes() is { } prefixBytes)
            range.Prefix = DecodeTextString(prefixBytes);

        if (doc.Resolve(dict.Get("St")).AsLong() is { } start && start > 0)
            range.StartValue = (uint)start;

        return range;
    }

    /// <summary>UTF-16BE when the BOM says so, PDFDocEncoding (≈ Latin-1) otherwise.</summary>
    private static string DecodeTextString(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            var sb = new StringBuilder();
            for (int i = 2; i + 1 < bytes.Length; i += 2) sb.Append((char)((bytes[i] << 8) | bytes[i + 1]));
            return sb.ToString();
        }
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
        return new string(chars);
    }
}
