// Ported from pdf_oxide `structure/parser.rs`: `decode_pdf_text_string` (102-127),
// `build_page_map` / `build_page_map_recursive` (141-205), `parse_structure_tree_inner`
// (225-353), `parse_struct_elem` (354-520), `parse_k_children` (521-724),
// `parse_marked_content_ref` (725-785) and `resolve_mcr_scope` (786-863), plus the
// `StructTreeRoot` / `StructElem` / `StructChild` shapes of `structure/types.rs`.
//
// Only the fields the /ActualText applier reads are carried: the element's own
// `/ActualText`, its `/Pg`, and its children. `/S` is still required, because a `/K` entry
// without it is not a structure element and has to fall through to the marked-content
// reference form. The type it names is not classified and `/RoleMap` is not read: nothing
// on this path distinguishes one structure type from another.
using System;
using System.Collections.Generic;
using System.Text;
using Xberg.Internal.Pdf;
using Xberg.Internal.PdfOxide.Fonts;

namespace Xberg.Internal.PdfOxide.Structure;

/// <summary>A `/K` child: either a nested element or a marked-content reference.</summary>
internal abstract class OxStructChild
{
    internal sealed class Elem : OxStructChild
    {
        public readonly OxStructElem Value;
        public Elem(OxStructElem value) => Value = value;
    }

    internal sealed class Mcr : OxStructChild
    {
        public readonly int Mcid;
        public readonly int Page;
        public readonly OxMcidScope Scope;
        public Mcr(int mcid, int page, OxMcidScope scope) { Mcid = mcid; Page = page; Scope = scope; }
    }
}

/// <summary>One `/StructElem` (§14.7.2).</summary>
internal sealed class OxStructElem
{
    public readonly List<OxStructChild> Children = new();

    /// <summary>Zero-based page index from `/Pg`, when it resolves to a page of this document.</summary>
    public int? Page;

    /// <summary>`/ActualText` (§14.9.4): the replacement for this element's whole subtree.</summary>
    public string? ActualText;
}

/// <summary>The document's `/StructTreeRoot`, as much of it as the ActualText index needs.</summary>
internal sealed class OxStructTreeRoot
{
    public readonly List<OxStructElem> RootElements = new();
}

internal static class OxStructTree
{
    /// <summary>
    /// Parse `/StructTreeRoot` from the catalog, or null when the document is untagged.
    /// </summary>
    /// <remarks>
    /// Upstream also parses when `/MarkInfo /Marked` is true and the catalog names no
    /// `/StructTreeRoot`, but that parse immediately returns "not a tagged PDF", so the
    /// catalog entry alone is the real gate.
    ///
    /// `/ParentTree` is deliberately not parsed: it only serves MCID → element reverse
    /// lookups, and the forward `/K` walk already carries the order this path needs.
    /// </remarks>
    public static OxStructTreeRoot? Parse(PdfDocument doc)
    {
        var catalog = doc.Catalog;
        if (catalog is null) return null;
        if (catalog.Get("StructTreeRoot") is not { } rootRef) return null;

        var pageMap = BuildPageMap(doc);

        var rootDict = doc.Resolve(rootRef).AsDict();
        // A non-dictionary here (a null from a corrupted parse, say) is "no structure tree",
        // not an error.
        if (rootDict is null) return null;

        var tree = new OxStructTreeRoot();

        if (rootDict.Get("K") is not { } kRaw) return tree;

        var visited = new HashSet<int>();
        var k = doc.Resolve(kRaw);
        if (k.AsArray() is { } arr)
        {
            foreach (var elemObj in arr.Items)
            {
                // Record root element ids before descending, so a back-reference from a
                // descendant to this root reads as a cycle.
                if (elemObj is PdfRef r && !visited.Add(r.Number)) continue;
                if (ParseStructElem(doc, elemObj, pageMap, visited) is { } elem)
                    tree.RootElements.Add(elem);
            }
        }
        else
        {
            if (ParseStructElem(doc, k, pageMap, visited) is { } elem)
                tree.RootElements.Add(elem);
        }

        return tree;
    }

    /// <summary>Page object number to zero-based page index, for resolving `/Pg`.</summary>
    private static Dictionary<int, int> BuildPageMap(PdfDocument doc)
    {
        var map = new Dictionary<int, int>();
        foreach (var kv in doc.PageNumbersByRef) map[kv.Key.Number] = kv.Value - 1;
        return map;
    }

    private static OxStructElem? ParseStructElem(
        PdfDocument doc,
        PdfObject? obj,
        Dictionary<int, int> pageMap,
        HashSet<int> visited)
    {
        var resolved = doc.Resolve(obj);
        var dict = resolved.AsDict();
        if (dict is null) return null;

        if (dict.Get("Type").AsName() is { } typeName)
        {
            if (typeName == "OBJR")
            {
                // §14.7.4 Table 323: an object-reference dictionary points at the real
                // element through /Obj, so dereference it and hand the caller that.
                if (dict.Get("Obj") is PdfRef objRef)
                {
                    if (!visited.Add(objRef.Number)) return null;
                    var target = doc.LoadObject(objRef.Number, objRef.Generation);
                    if (target is null) return null;
                    return ParseStructElem(doc, target, pageMap, visited);
                }
                return null;
            }
            if (typeName != "StructElem") return null;
        }

        // /S is required; without it this is not a structure element and the caller falls
        // back to reading the dictionary as a marked-content reference.
        if (dict.Get("S").AsName() is null) return null;

        var elem = new OxStructElem();

        if (dict.Get("Pg") is PdfRef pgRef && pageMap.TryGetValue(pgRef.Number, out int pageNum))
            elem.Page = pageNum;

        if (dict.Get("ActualText") is { } atObj && doc.Resolve(atObj).AsStringBytes() is { } atBytes)
        {
            string text = DecodePdfTextString(atBytes);
            if (text.Length != 0) elem.ActualText = text;
        }

        if (dict.Get("K") is { } kRaw)
        {
            if (kRaw is PdfRef kRef)
            {
                // Resolving first would lose the object id, and the dictionary arm below
                // could then not detect a cycle — so claim the id while it is still here.
                var kResolved = doc.LoadObject(kRef.Number, kRef.Generation);
                if (kResolved is null) return elem;
                if (kResolved.AsDict() is not null && !visited.Add(kRef.Number)) return elem;
                ParseKChildren(doc, kResolved, elem, pageMap, visited);
            }
            else
            {
                ParseKChildren(doc, doc.Resolve(kRaw), elem, pageMap, visited);
            }
        }

        return elem;
    }

    private static void ParseKChildren(
        PdfDocument doc,
        PdfObject kObj,
        OxStructElem parent,
        Dictionary<int, int> pageMap,
        HashSet<int> visited)
    {
        switch (kObj)
        {
            case PdfNumber { IsInteger: true } mcidNum:
            {
                // A bare integer child names an MCID in the page's own content stream; the
                // /MCR dictionary form is reserved for cross-stream references (§14.7.5.4.2).
                int page = parent.Page ?? 0;
                parent.Children.Add(new OxStructChild.Mcr((int)mcidNum.AsLong, page, OxMcidScope.Page(page)));
                break;
            }

            case PdfArray arr:
            {
                foreach (var childRaw in arr.Items)
                {
                    // Claim the id before resolving: an already-visited reference would
                    // resolve to a dictionary and slip through with no id left to check.
                    if (childRaw is PdfRef childRef && !visited.Add(childRef.Number)) continue;

                    var child = doc.Resolve(childRaw);
                    switch (child)
                    {
                        case PdfNumber { IsInteger: true } mcidNum:
                        {
                            int page = parent.Page ?? 0;
                            parent.Children.Add(
                                new OxStructChild.Mcr((int)mcidNum.AsLong, page, OxMcidScope.Page(page)));
                            break;
                        }
                        case PdfDict:
                        case PdfStream:
                        {
                            if (ParseStructElem(doc, child, pageMap, visited) is { } childElem)
                                parent.Children.Add(new OxStructChild.Elem(childElem));
                            else if (ParseMarkedContentRef(doc, child, pageMap) is { } mcr)
                                parent.Children.Add(mcr);
                            break;
                        }
                        case PdfRef innerRef:
                        {
                            // Doubly indirect child: one Resolve only follows one hop.
                            if (!visited.Add(innerRef.Number)) break;
                            var target = doc.LoadObject(innerRef.Number, innerRef.Generation);
                            if (target is null) break;
                            if (ParseStructElem(doc, target, pageMap, visited) is { } innerElem)
                                parent.Children.Add(new OxStructChild.Elem(innerElem));
                            else if (ParseMarkedContentRef(doc, target, pageMap) is { } innerMcr)
                                parent.Children.Add(innerMcr);
                            break;
                        }
                    }
                }
                break;
            }

            case PdfDict:
            case PdfStream:
            {
                if (ParseStructElem(doc, kObj, pageMap, visited) is { } childElem)
                    parent.Children.Add(new OxStructChild.Elem(childElem));
                else if (ParseMarkedContentRef(doc, kObj, pageMap) is { } mcr)
                    parent.Children.Add(mcr);
                break;
            }

            case PdfRef kRef:
            {
                if (!visited.Add(kRef.Number)) break;
                var target = doc.LoadObject(kRef.Number, kRef.Generation);
                if (target is null) break;
                if (ParseStructElem(doc, target, pageMap, visited) is { } refElem)
                    parent.Children.Add(new OxStructChild.Elem(refElem));
                else if (ParseMarkedContentRef(doc, target, pageMap) is { } refMcr)
                    parent.Children.Add(refMcr);
                break;
            }
        }
    }

    /// <summary>
    /// A marked-content reference dictionary (§14.7.5.4.2): `/MCID` is required, `/Pg`
    /// names the page (defaulting to page 0), and `/Stm` names the content stream that
    /// holds the MCID when it is not the page's own.
    /// </summary>
    private static OxStructChild? ParseMarkedContentRef(
        PdfDocument doc, PdfObject obj, Dictionary<int, int> pageMap)
    {
        var dict = obj.AsDict();
        if (dict is null) return null;

        if (dict.Get("Type").AsName() is { } typeName && typeName != "MCR") return null;

        if (dict.Get("MCID") is not PdfNumber mcidNum) return null;
        int mcid = (int)mcidNum.AsLong;

        int page = 0;
        if (dict.Get("Pg") is PdfRef pgRef && pageMap.TryGetValue(pgRef.Number, out int resolvedPage))
            page = resolvedPage;

        return new OxStructChild.Mcr(mcid, page, ResolveMcrScope(doc, dict, page));
    }

    /// <summary>
    /// The MCID's content-stream scope. `/Stm` absent means the page's own stream; a Form
    /// XObject or a tiling pattern each own their MCID namespace. An unclassifiable `/Stm`
    /// falls back to page scope: at worst the lookup misses and the raw glyphs flow through,
    /// which is safer than a collision between two namespaces.
    /// </summary>
    private static OxMcidScope ResolveMcrScope(PdfDocument doc, PdfDict mcrDict, int page)
    {
        if (mcrDict.Get("Stm") is not { } stm) return OxMcidScope.Page(page);
        if (stm is not PdfRef stmRef) return OxMcidScope.Page(page);

        var stmObj = doc.LoadObject(stmRef.Number, stmRef.Generation);
        var streamDict = stmObj.AsDict();
        if (streamDict is null) return OxMcidScope.Page(page);

        // /Type is optional on an XObject stream (§8.8), so producers do omit it; /Subtype
        // alone identifies the form.
        if (streamDict.Get("Subtype").AsName() == "Form")
            return OxMcidScope.Form(stmRef.Number, stmRef.Generation);

        // A tiling pattern (§8.7.3.3) has a content stream of its own; a shading pattern
        // (PatternType 2) does not and so cannot host an MCID.
        if (streamDict.Get("PatternType").AsLong() == 1)
            return OxMcidScope.Pattern(stmRef.Number, stmRef.Generation);

        return OxMcidScope.Page(page);
    }

    /// <summary>
    /// Decode a text string (§7.9.2) the way the structure parser does: UTF-16 with either
    /// BOM, otherwise PDFDocEncoding. Deliberately without the extractor's UTF-8 guess —
    /// structure-tree strings come from tagging tools that write the spec's encodings.
    /// </summary>
    internal static string DecodePdfTextString(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return DecodeUtf16(bytes, bigEndian: true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return DecodeUtf16(bytes, bigEndian: false);

        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
            if (OxEncodingTables.PdfDocEncodingLookup(b) is { } mapped)
                sb.Append(mapped);
        return sb.ToString();
    }

    private static string DecodeUtf16(byte[] bytes, bool bigEndian)
    {
        // A trailing odd byte is dropped rather than treated as an error.
        int units = (bytes.Length - 2) / 2;
        var sb = new StringBuilder(units);
        for (int i = 0; i < units; i++)
        {
            int lo = 2 + (i * 2);
            sb.Append((char)(bigEndian
                ? (bytes[lo] << 8) | bytes[lo + 1]
                : (bytes[lo + 1] << 8) | bytes[lo]));
        }
        return sb.ToString();
    }
}
