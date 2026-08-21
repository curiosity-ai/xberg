// Ported from crates/xberg/src/extraction/math_symbols.rs
// Shared Unicode-to-LaTeX symbol mapping for the math converters, so a given symbol maps to the
// same LaTeX command whatever source format it came from.

using System.Text;

// The namespace deliberately differs from the directory name: a `Xberg.Internal.Math` namespace
// would shadow `System.Math` for every file under `Xberg.Internal.*`.
namespace Xberg.Internal.MathMarkup;

internal static class MathSymbols
{
    /// <summary>
    /// Render run text, mapping Unicode math symbols to LaTeX commands and escaping TeX
    /// structural characters.
    /// </summary>
    /// <remarks>
    /// Source text can carry literal <c>{</c>, <c>}</c>, <c>&amp;</c>, <c>\</c> and friends (a
    /// stretchy <c>&lt;mo&gt;{&lt;/mo&gt;</c> cases brace, a set-difference backslash in
    /// <c>&lt;mi&gt;</c>). Passed through raw they change the LaTeX <em>structure</em> — an
    /// unpaired brace makes the whole formula unparseable. Escaped they render as the glyphs the
    /// source displayed.
    /// </remarks>
    public static void RenderRunText(string text, StringBuilder output)
    {
        var runes = new List<Rune>();
        foreach (var rune in text.EnumerateRunes()) runes.Add(rune);

        for (int i = 0; i < runes.Count; i++)
        {
            // A combining accent applies to the character before it. Sources write `U̅`
            // (U + combining overline) inside identifiers; KaTeX rejects the raw mark, so fold
            // the pair into the accent macro.
            if (i + 1 < runes.Count && CombiningAccentToLatex(runes[i + 1]) is { } pairCmd)
            {
                i++;
                output.Append(pairCmd).Append('{');
                PushMappedRune(runes[i - 1], output);
                output.Append('}');
                continue;
            }
            if (CombiningAccentToLatex(runes[i]) is { } cmd)
            {
                // A mark with no base in this run applies to whatever the output currently ends
                // with (the source split base and mark across elements, e.g.
                // `<mi>Σ</mi><mo>̅</mo>`).
                WrapTrailingAtom(cmd, output);
                continue;
            }
            PushMappedRune(runes[i], output);
        }
    }

    /// <summary>Map one rune through the symbol table / structural escapes onto <paramref name="output"/>.</summary>
    private static void PushMappedRune(Rune rune, StringBuilder output)
    {
        if (UnicodeToLatex(rune) is { } latex) output.Append(latex);
        else if (EscapeTexStructural(rune) is { } escaped) output.Append(escaped);
        else output.Append(rune.ToString());
    }

    /// <summary>Map a combining accent character to its LaTeX accent macro.</summary>
    private static string? CombiningAccentToLatex(Rune rune) => rune.Value switch
    {
        0x0302 => "\\hat",
        0x0303 => "\\tilde",
        0x0304 or 0x0305 => "\\bar",
        0x0306 => "\\breve",
        0x0307 => "\\dot",
        0x0308 => "\\ddot",
        0x030A => "\\mathring",
        0x030C => "\\check",
        0x0332 => "\\underline",
        0x20D7 => "\\vec",
        _ => null,
    };

    /// <summary>
    /// Wrap the trailing atom of <paramref name="output"/> (one trailing LaTeX command, else one
    /// trailing character) in <c>cmd{...}</c>. No-op on empty output.
    /// </summary>
    private static void WrapTrailingAtom(string cmd, StringBuilder output)
    {
        string all = output.ToString();
        int trimmedLen = all.TrimEnd().Length;
        string trailingWs = all[trimmedLen..];
        string body = all[..trimmedLen];

        int atomStart;
        int backslash = body.LastIndexOf('\\');
        if (backslash >= 0 && backslash + 1 < body.Length && AllAsciiLetters(body, backslash + 1))
        {
            atomStart = backslash;
        }
        else if (body.Length == 0)
        {
            return;
        }
        else
        {
            atomStart = body.Length - 1;
            if (char.IsLowSurrogate(body[atomStart]) && atomStart > 0) atomStart--;
        }

        // Never wrap a structural character: losing the accent is better than producing an
        // unbalanced group.
        string atom = body[atomStart..];
        if (atom is "{" or "}" or "\\" or "^" or "_" or "&") return;

        output.Clear();
        output.Append(body, 0, atomStart).Append(cmd).Append('{').Append(atom).Append('}').Append(trailingWs);
    }

    private static bool AllAsciiLetters(string s, int from)
    {
        for (int i = from; i < s.Length; i++)
            if (!char.IsAsciiLetter(s[i])) return false;
        return true;
    }

    /// <summary>Escape a TeX structural character appearing as literal content.</summary>
    public static string? EscapeTexStructural(Rune rune) => rune.Value switch
    {
        '{' => "\\{",
        '}' => "\\}",
        '&' => "\\&",
        '%' => "\\%",
        '#' => "\\#",
        '$' => "\\$",
        '_' => "\\_",
        '\\' => "\\backslash ",
        _ => null,
    };

    /// <summary>Map a Unicode character to its LaTeX command (if any).</summary>
    public static string? UnicodeToLatex(Rune rune) => rune.Value switch
    {
        0x03B1 => "\\alpha ",
        0x03B2 => "\\beta ",
        0x03B3 => "\\gamma ",
        0x03B4 => "\\delta ",
        0x03B5 => "\\epsilon ",
        0x03B6 => "\\zeta ",
        0x03B7 => "\\eta ",
        0x03B8 => "\\theta ",
        0x03B9 => "\\iota ",
        0x03BA => "\\kappa ",
        0x03BB => "\\lambda ",
        0x03BC => "\\mu ",
        0x03BD => "\\nu ",
        0x03BE => "\\xi ",
        0x03BF => "o",
        0x03C0 => "\\pi ",
        0x03C1 => "\\rho ",
        0x03C2 => "\\varsigma ",
        0x03C3 => "\\sigma ",
        0x03C4 => "\\tau ",
        0x03C5 => "\\upsilon ",
        0x03C6 => "\\phi ",
        0x03C7 => "\\chi ",
        0x03C8 => "\\psi ",
        0x03C9 => "\\omega ",
        0x0393 => "\\Gamma ",
        0x0394 => "\\Delta ",
        0x0398 => "\\Theta ",
        0x039B => "\\Lambda ",
        0x039E => "\\Xi ",
        0x03A0 => "\\Pi ",
        0x03A3 => "\\Sigma ",
        0x03A5 => "\\Upsilon ",
        0x03A6 => "\\Phi ",
        0x03A8 => "\\Psi ",
        0x03A9 => "\\Omega ",
        0x00B1 => "\\pm ",
        0x2213 => "\\mp ",
        0x00D7 => "\\times ",
        0x00F7 => "\\div ",
        0x22C5 => "\\cdot ",
        0x2217 => "\\ast ",
        0x2218 => "\\circ ",
        0x2219 => "\\bullet ",
        0x2264 => "\\leq ",
        0x2265 => "\\geq ",
        0x2260 => "\\neq ",
        0x2248 => "\\approx ",
        0x2261 => "\\equiv ",
        0x227A => "\\prec ",
        0x227B => "\\succ ",
        0x2286 => "\\subseteq ",
        0x2287 => "\\supseteq ",
        0x2282 => "\\subset ",
        0x2283 => "\\supset ",
        0x2208 => "\\in ",
        0x2209 => "\\notin ",
        0x220B => "\\ni ",
        0x2190 => "\\leftarrow ",
        0x2192 => "\\rightarrow ",
        0x2191 => "\\uparrow ",
        0x2193 => "\\downarrow ",
        0x2194 => "\\leftrightarrow ",
        0x21D0 => "\\Leftarrow ",
        0x21D2 => "\\Rightarrow ",
        0x21D4 => "\\Leftrightarrow ",
        0x21A6 => "\\mapsto ",
        0x221E => "\\infty ",
        0x2202 => "\\partial ",
        0x2207 => "\\nabla ",
        0x2200 => "\\forall ",
        0x2203 => "\\exists ",
        0x2205 => "\\emptyset ",
        0x2227 => "\\wedge ",
        0x2228 => "\\vee ",
        0x00AC => "\\neg ",
        0x2229 => "\\cap ",
        0x222A => "\\cup ",
        0x2026 => "\\ldots ",
        0x22EF => "\\cdots ",
        0x22EE => "\\vdots ",
        0x22F1 => "\\ddots ",
        0x2032 => "'",
        0x2033 => "''",
        0x210F => "\\hbar ",
        0x2113 => "\\ell ",
        0x211C => "\\Re ",
        0x2111 => "\\Im ",
        0x2118 => "\\wp ",
        0x2135 => "\\aleph ",
        0x2016 or 0x2225 => "\\Vert ",
        0x2223 => "\\mid ",
        0x2329 or 0x27E8 => "\\langle ",
        0x232A or 0x27E9 => "\\rangle ",
        0x204E => "\\ast ",
        0x03D2 => "\\Upsilon ",
        0x2211 => "\\sum ",
        0x220F => "\\prod ",
        0x222B => "\\int ",
        0x222C => "\\iint ",
        0x222D => "\\iiint ",
        0x222E => "\\oint ",
        0x2210 => "\\coprod ",
        0x22C0 => "\\bigwedge ",
        0x22C1 => "\\bigvee ",
        0x22C2 => "\\bigcap ",
        0x22C3 => "\\bigcup ",
        _ => null,
    };
}
