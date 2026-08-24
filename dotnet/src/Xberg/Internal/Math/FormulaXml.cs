// Ported from crates/xberg/src/extraction/formula_xml.rs.
//
// JATS and DocBook both wrap an equation in an element that may hold verbatim TeX, a MathML
// subtree, or plain text. The capture below reads one such element and returns its LaTeX, so
// each format states only the names of its own child elements.
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xberg.Extractors;

namespace Xberg.Internal.MathMarkup;

/// <summary>The child elements a format uses inside a formula.</summary>
/// <param name="Tex">
/// Element whose text is verbatim TeX and wins over MathML. JATS names it <c>tex-math</c>;
/// DocBook names it <c>alt</c>.
/// </param>
/// <param name="Label">
/// Element whose text is the equation number, which becomes a LaTeX <c>\tag</c>. <c>null</c>
/// for a format that has no such element.
/// </param>
internal readonly record struct FormulaElements(string Tex, string? Label);

internal static class FormulaXml
{
    /// <summary>Return the local part of a possibly prefixed XML qualified name.</summary>
    private static string LocalNameOf(string qname)
    {
        int colon = qname.LastIndexOf(':');
        return colon < 0 ? qname : qname[(colon + 1)..];
    }

    /// <summary>
    /// Append a start tag with its prefix stripped and its namespace declarations dropped, so
    /// the captured subtree parses without the document's namespace context.
    /// </summary>
    /// <remarks>
    /// Attribute values are re-escaped, so a single-quoted source attribute holding a double
    /// quote stays well-formed. When two names collide after prefix stripping the first wins:
    /// a duplicate attribute would make the captured XML unparseable.
    /// </remarks>
    private static void WriteStartTag(StringBuilder buf, XmlToken tag, bool selfClosing)
    {
        buf.Append('<').Append(LocalNameOf(tag.Name));
        var written = new List<string>();
        foreach (var (key, value) in tag.Attrs ?? new List<(string, string)>())
        {
            if (key == "xmlns" || key.StartsWith("xmlns:", System.StringComparison.Ordinal)) continue;
            string localKey = LocalNameOf(key);
            if (written.Contains(localKey)) continue;
            buf.Append(' ').Append(localKey).Append("=\"").Append(EscapeXml(value)).Append('"');
            written.Add(localKey);
        }
        if (selfClosing) buf.Append('/');
        buf.Append('>');
    }

    /// <summary>The five characters quick-xml's escaper writes as references.</summary>
    private static string EscapeXml(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Strip a full LaTeX document wrapper from verbatim TeX. PMC articles commonly ship
    /// <c>&lt;tex-math&gt;</c> as a complete compilable document
    /// (<c>\documentclass…\begin{document}$$…$$\end{document}</c>); only the body is the formula.
    /// </summary>
    private static string StripLatexDocumentWrapper(string tex)
    {
        if (!tex.Contains("\\documentclass")) return tex;
        int start = tex.IndexOf("\\begin{document}", System.StringComparison.Ordinal);
        if (start < 0) return tex;
        string body = tex[(start + "\\begin{document}".Length)..];
        int end = body.IndexOf("\\end{document}", System.StringComparison.Ordinal);
        if (end >= 0) body = body[..end];
        return body.Trim();
    }

    /// <summary>
    /// Extract the LaTeX for a formula subtree. The caller has consumed the formula's start tag.
    /// </summary>
    /// <remarks>
    /// The preference order is the TeX element's text verbatim, then the <c>math</c> subtree
    /// converted with the shared MathML converter, then the flattened text content.
    /// </remarks>
    public static string ExtractFormulaLatex(XmlPullReader reader, FormulaElements names)
    {
        var fallbackText = new StringBuilder();
        var texMath = new StringBuilder();
        var label = new StringBuilder();
        var mathmlXmls = new List<string>();
        StringBuilder? capture = null;
        int captureDepth = 0;
        bool captureInAlternatives = false;
        int alternativesDepth = 0;
        bool alternativesMathSeen = false;
        bool inTexMath = false;
        bool inLabel = false;
        int depth = 0;

        while (true)
        {
            var ev = reader.Read();
            if (ev.Kind == XmlEv.Eof) break;

            if (ev.Kind == XmlEv.Start)
            {
                depth++;
                string local = LocalNameOf(ev.Name);
                if (capture is not null)
                {
                    captureDepth++;
                    WriteStartTag(capture, ev, false);
                }
                else if (local == "math")
                {
                    capture = new StringBuilder();
                    WriteStartTag(capture, ev, false);
                    captureDepth = 1;
                    captureInAlternatives = alternativesDepth > 0;
                }
                else if (local == "alternatives") alternativesDepth++;
                else if (local == names.Tex) inTexMath = true;
                else if (names.Label is not null && local == names.Label) inLabel = true;
            }
            else if (ev.Kind == XmlEv.Empty)
            {
                if (capture is not null) WriteStartTag(capture, ev, true);
            }
            else if (ev.Kind == XmlEv.End)
            {
                if (capture is not null)
                {
                    capture.Append("</").Append(LocalNameOf(ev.Name)).Append('>');
                    captureDepth--;
                    if (captureDepth == 0)
                    {
                        string xml = capture.ToString();
                        capture = null;
                        // Inside `<alternatives>` every `math` sibling is one more representation
                        // of the SAME formula, so the first wins. Outside, each sibling is its own
                        // equation.
                        if (captureInAlternatives)
                        {
                            if (!alternativesMathSeen) { alternativesMathSeen = true; mathmlXmls.Add(xml); }
                        }
                        else mathmlXmls.Add(xml);
                    }
                }
                else
                {
                    string local = LocalNameOf(ev.Name);
                    if (local == names.Tex) inTexMath = false;
                    else if (names.Label is not null && local == names.Label) inLabel = false;
                    else if (local == "alternatives" && alternativesDepth > 0) alternativesDepth--;
                }
                if (depth == 0) break;
                depth--;
            }
            else if (ev.Kind == XmlEv.Text)
            {
                if (ev.Text.Trim().Length == 0) continue;
                if (capture is not null) capture.Append(EscapeXml(ev.Text));
                else if (inTexMath) texMath.Append(ev.Text);
                else if (inLabel)
                {
                    if (label.Length > 0) label.Append(' ');
                    label.Append(ev.Text);
                }
                else fallbackText.Append(ev.Text).Append(' ');
            }
            else if (ev.Kind == XmlEv.CData)
            {
                if (ev.Text.Trim().Length == 0) continue;
                // CDATA holding TeX goes in verbatim; the capture path escapes it because it is
                // being written back out as XML.
                if (inTexMath) texMath.Append(ev.Text);
                else if (capture is not null) capture.Append(EscapeXml(ev.Text));
                else fallbackText.Append(ev.Text).Append(' ');
            }
        }

        // An equation label (`<label>1.1</label>`) becomes a LaTeX `\tag` so the equation number
        // survives the conversion. `\tag` renders inside parens, so a source label that already
        // carries them (`(1)`) sheds one pair — otherwise it displays as `((1))`.
        string WithTag(string latex)
        {
            string lbl = label.ToString().Trim();
            if (lbl.Length >= 2 && lbl[0] == '(' && lbl[^1] == ')') lbl = lbl[1..^1].Trim();
            lbl = new string(lbl.Where(c => c != '{' && c != '}').ToArray());
            return lbl.Length == 0 ? latex : $"{latex} \\tag{{{lbl}}}";
        }

        string tex = MathMl.StripMathDelimiters(StripLatexDocumentWrapper(texMath.ToString().Trim()));
        if (tex.Length > 0) return WithTag(tex);

        if (mathmlXmls.Count > 0)
        {
            var parts = new List<string>();
            foreach (var xml in mathmlXmls)
            {
                string latex = MathMl.ConvertMathmlStrToLatex(xml);
                if (latex.Trim().Length > 0) parts.Add(latex.Trim());
            }
            if (parts.Count > 0) return WithTag(string.Join(" \\\\ ", parts));
        }

        string fallback = fallbackText.ToString();
        if (label.ToString().Trim().Length > 0) fallback = $"{label.ToString().Trim()} {fallback}";
        return fallback.Trim();
    }
}
