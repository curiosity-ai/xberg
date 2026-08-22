// The span merger's space decision, ported from pdf_oxide-0.3.77
//   src/extractors/text.rs lines 1190-1857:
//     should_insert_space, has_boundary_space, build_boundary_characters,
//     is_email_context, is_citation_context.
//
// ISO 32000-1:2008 §9.10 is explicit that PDF does not encode word boundaries, so
// this is a chain of numbered rules with early returns and the ORDER is the
// behaviour: a rule that fires first denies every rule below it the chance to
// speak. Only spec-defined signals participate — whitespace already in the string
// (§9.4.3), TJ array offsets (§9.4.4), and glyph geometry (§9.4) — plus the
// script and typography guards that exist to stop those three signals misfiring.
//
// Rust walks text with `chars()` (Unicode scalar values) and measures `len()` in
// UTF-8 bytes; both are reproduced here — runes for the character rules, UTF-8
// byte counts where upstream deliberately used byte length as an O(1) proxy for
// character count.
using System.Globalization;
using System.Text;

namespace Xberg.Internal.PdfOxide.Text;

internal static class OxSpaceDecisionRules
{
    /// <summary>
    /// Decide whether a space belongs between two adjacent glyph runs (text.rs:1190).
    /// </summary>
    /// <param name="fonts">
    /// Stands in for upstream's <c>&amp;HashMap&lt;String, Arc&lt;FontInfo&gt;&gt;</c>. A null
    /// <see cref="IOxSpanFonts.SpaceGlyphWidth"/> is the <c>contains_key</c> miss: the
    /// threshold falls back to a quarter of the font size and the kerning guard turns off.
    /// </param>
    internal static OxSpaceDecision ShouldInsertSpace(
        string precedingText,
        string followingText,
        float gapPt,
        float fontSize,
        string fontName,
        IOxSpanFonts fonts,
        bool tjOffsetTriggered,
        OxSpanMergingConfig config,
        OxRect? prevBbox,
        OxRect? nextBbox,
        float prevFontSize,
        float nextFontSize)
    {
        Rune? prevLast = LastRune(precedingText);
        Rune? nextFirst = FirstRune(followingText);

        // Rule 0: a space the producer already emitted (§9.4.3) must not be doubled.
        if (HasBoundarySpace(precedingText, followingText))
        {
            return OxSpaceDecision.NoSpace(OxSpaceSource.AlreadyPresent, 1.0f);
        }

        // Rule 0.3: complex-script combining-mark guard. A Brahmic/Thai/Khmer dependent
        // vowel sign, virama or tone mark carries its own advance, so the geometric and
        // consensus paths below — neither of which consults WordBoundaryDetector — would
        // read the intra-word matra-to-consonant gap as a word space. A genuine break in
        // these scripts carries an explicit space glyph and was caught by Rule 0.
        if (prevLast is Rune pcMark && nextFirst is Rune ncMark
            && ScriptSignals.IsComplexScriptMark(pcMark.Value)
            && ScriptSignals.DetectComplexScript(ncMark.Value).HasValue)
        {
            return OxSpaceDecision.NoSpace(OxSpaceSource.NoSpace, 0.9f);
        }

        // Rule 0.4: emoji-to-letter. A pictograph advances so far that the residual gap to
        // the next token lands under the proportional-font space threshold and the space is
        // lost ("[emoji]README"). Requiring the following scalar to be alphabetic already
        // excludes ZWJ/variation-selector sequences, whose next scalar is a selector or
        // another pictograph, so any non-negative gap is a safe gate.
        if (gapPt >= 0.0f
            && prevLast is Rune pcPict && OxTextHelpers.IsPictographic(pcPict)
            && nextFirst is Rune ncPict && IsAlphabetic(ncPict))
        {
            return OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.85f);
        }

        // Rule 0.5: an email address is one token to a reader (§9.10), so the gap has to
        // clear a much wider bar before it may split "user@" from "domain.com".
        if (config.DetectEmailPatterns && IsEmailContext(precedingText, followingText))
        {
            float emailBase = fonts.SpaceGlyphWidth(fontName) is float emailSpaceUnits
                ? (emailSpaceUnits / 1000.0f) * fontSize * 0.5f
                : fontSize * 0.25f;

            return gapPt > emailBase * config.EmailThresholdMultiplier
                ? OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.85f)
                : OxSpaceDecision.NoSpace(OxSpaceSource.NoSpace, 1.0f);
        }

        // Rule 1: line breaks. Two spans on one line share a baseline whatever their
        // heights, so the Y origins — not a bottom-to-top gap — are what reveal the wrap.
        // A word hyphenated across the break continues; any other wrap starts a new word.
        if (prevBbox is OxRect prevBoxLine && nextBbox is OxRect nextBoxLine)
        {
            float yDiff = MathF.Abs(prevBoxLine.Y - nextBoxLine.Y);
            float lineBreakThreshold = fontSize * 0.5f;

            if (yDiff > lineBreakThreshold)
            {
                // Guards against reading a column jump as a wrap: a real wrap returns to
                // roughly the same left edge.
                bool sameColumn = MathF.Abs(prevBoxLine.Left - nextBoxLine.Left) < fontSize * 2.0f;

                if (sameColumn)
                {
                    return precedingText.EndsWith('-')
                        ? OxSpaceDecision.NoSpace(OxSpaceSource.NoSpace, 1.0f)
                        : OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.9f);
                }
            }
        }

        // Rule 1.5: superscript citation markers sit outside the running text, so a single
        // signal is enough — waiting for consensus fuses the marker into the word.
        if (config.DetectCitationMarkers
            && IsCitationContext(prevBbox, nextBbox, fontSize, prevFontSize, nextFontSize))
        {
            float citationThreshold = fonts.SpaceGlyphWidth(fontName) is float citationSpaceUnits
                ? (citationSpaceUnits / 1000.0f) * fontSize * 0.5f
                : fontSize * 0.25f;

            if (tjOffsetTriggered || gapPt > citationThreshold)
            {
                return OxSpaceDecision.Insert(OxSpaceSource.TjOffset, 0.90f);
            }
        }

        // Rule 2: the font-aware geometric threshold every rule below is measured against.
        // TJ offsets are typographic hints, not word boundaries (§9.4.4), so the rules that
        // follow require the offset and the geometry to agree — or the geometry to be
        // strong enough on its own.
        float geometricThreshold;
        float? spaceGlyphWidth = fonts.SpaceGlyphWidth(fontName);
        if (spaceGlyphWidth is float spaceWidthUnits)
        {
            float spaceWidthPt = (spaceWidthUnits / 1000.0f) * fontSize;

            // Monospace producers emit one show-text op per glyph at one-em advances, so
            // ordinary intra-token gaps briefly clear the proportional bar and punctuation
            // in code listings comes out as "function add (a , b )".
            float wordMarginRatio = OxTextHelpers.IsMonospaceFont(fontName) ? 1.2f : 0.5f;

            // A font-size change marks a font-run boundary (italic to roman, family switch)
            // where pdfTeX-class writers omit the space glyph entirely, as in
            // "Astronomy & Astrophysicsmanuscript no."; 30% less evidence is demanded there.
            // Only the size-changing subset is caught — many italic transitions keep the size.
            if (MathF.Abs(prevFontSize - nextFontSize) > 0.5f)
            {
                wordMarginRatio *= 0.7f;
            }

            geometricThreshold = spaceWidthPt * wordMarginRatio;
        }
        else
        {
            geometricThreshold = fontSize * 0.25f;
        }

        // Rule 2.5: AGL ligature boundary. pdfTeX emits U+FB00..U+FB06 as its own cluster
        // inside a word ("di" + ligature + "cult"), and the kerning that surrounds it is an
        // emission artefact, not a word gap; 1.5x the threshold suppresses "di ff cult".
        bool ligatureBoundary = OxTextHelpers.StartsWithAglLigature(followingText)
            || (prevLast is Rune pcLig && pcLig.Value >= 0xFB00 && pcLig.Value <= 0xFB06);
        if (ligatureBoundary)
        {
            geometricThreshold *= 1.5f;
        }

        bool geometricSuggestsSpace = gapPt > geometricThreshold;

        // Rule 3: intra-word kerning guard. TJ-heavy producers (LaTeX, Word) hand this
        // function clusters like "cha"+"nge" whose gap sits just above the threshold but
        // well below a real word gap, and the consensus rules below would then space them.
        //
        // The 1.5x ceiling (0.75 space-glyph widths, ~0.2em for a typical 0.25em space) is
        // the widest realistic microtype/Word letter-spacing. The earlier 2.4x ceiling was
        // wider than any real kerning and glued tight word gaps ("MasterofScience") on PDFs
        // that position words with small Td offsets. Gaps in the overlap zone — wide letter
        // tracking in titles, ~0.28em — are not separable by magnitude and fall through.
        //
        // Both sides must be lowercase: LaTeX intra-word kerning happens inside lowercase
        // runs, while real boundaries in professional PDFs often involve capitals, which
        // must reach consensus instead so "APPENDIXA" does not fuse.
        //
        // It fires regardless of tjOffsetTriggered, since the gap can be geometric-only,
        // and only when the font is known — the no-font fallback is a deliberately wider,
        // more conservative threshold that already separates kerning from word gaps.
        if (spaceGlyphWidth.HasValue && gapPt < geometricThreshold * 1.5f
            && prevLast is Rune pcKern && nextFirst is Rune ncKern
            && Rune.IsLower(pcKern) && Rune.IsLower(ncKern))
        {
            return OxSpaceDecision.NoSpace(OxSpaceSource.IntraWordKerning, 0.9f);
        }

        // Rule 4: consensus. Both signals agreeing is the strongest evidence available, and
        // demanding it keeps justified text, where TJ offsets are arbitrary, from being
        // spaced at every glyph.
        if (tjOffsetTriggered && geometricSuggestsSpace)
        {
            return OxSpaceDecision.Insert(OxSpaceSource.TjOffset, 1.0f);
        }

        // Rule 5: an explicit TJ offset earns a relaxed geometric bar (25% of a space
        // glyph), because tight typesetting such as LaTeX papers sets word gaps narrower
        // than the standard 50%.
        if (tjOffsetTriggered && gapPt > geometricThreshold * 0.5f)
        {
            return OxSpaceDecision.Insert(OxSpaceSource.TjOffset, 0.9f);
        }

        // Rule 6: when the two signals contradict each other, WordBoundaryDetector breaks
        // the tie on the script evidence neither signal carries (§9.4.4).
        if (tjOffsetTriggered != geometricSuggestsSpace
            && prevBbox is OxRect prevBoxTie && nextBbox is OxRect nextBoxTie)
        {
            (List<CharacterInfo> characters, BoundaryContext context) = BuildBoundaryCharacters(
                precedingText, followingText, prevBoxTie, nextBoxTie, fontSize, tjOffsetTriggered);

            // Detecting the document script first lets the detector skip the analyses that
            // cannot apply to these two glyphs.
            var detector = new WordBoundaryDetector()
                .WithDocumentScript(DocumentScriptDetector.DetectFromCharacters(characters))
                .WithGeometricGapRatio(0.5f);

            if (detector.DetectWordBoundaries(characters, context).Count > 0)
            {
                return OxSpaceDecision.Insert(OxSpaceSource.WordBoundaryAnalysis, 0.85f);
            }
        }

        // Rule 7: strong geometry alone. The threshold is already half a space-glyph
        // advance, the same bar pdfium uses by default; the earlier 2x multiplier demanded
        // a full space glyph, which is stricter than the 60-80% gaps modern tight
        // typesetters emit and glued "atBirmingham" / "proteincrystals". Kerning and
        // letter-spacing stay well under 50% of a space glyph, so this does not break words
        // apart; digit runs are protected separately in Rule 8.
        if (gapPt > geometricThreshold)
        {
            return OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.95f);
        }

        // Rule 8: separate value tokens. Adjacent table cells like "$0.00" "$0.00" leave
        // gaps of 1-2pt that miss the threshold above, yet fragments of one word have
        // essentially zero gap — so any positive gap between things that look like distinct
        // values is a boundary.
        const float MinTokenGap = 0.01f;
        if (gapPt > MinTokenGap && prevLast is Rune pcTok && nextFirst is Rune ncTok)
        {
            bool prevIsValueEnd = IsAsciiDigit(pcTok) || pcTok.Value is '%' or ')' or ']';

            // A long number split across spans by kerning or TJ rounding can show a tiny
            // positive gap, which must not become "123 456"; below half the geometric
            // threshold that is intra-number kerning, not a token boundary.
            bool digitDigit = IsAsciiDigit(ncTok) && IsAsciiDigit(pcTok);
            bool digitDigitGapOk = !digitDigit || gapPt > geometricThreshold * 0.5f;

            bool nextIsValueStart = ncTok.Value is '$' or '(' or '['
                || (ncTok.Value == '-' && Utf8Length(followingText) > 1)
                || (IsAsciiDigit(ncTok) && prevIsValueEnd && digitDigitGapOk);

            // "Subtotal" + "$500.00" — a currency symbol after any word or number.
            bool textThenCurrency = (IsAsciiAlphabetic(pcTok) || IsAsciiDigit(pcTok))
                && ncTok.Value is '$' or '€' or '£';

            if ((prevIsValueEnd && nextIsValueStart) || textThenCurrency)
            {
                return OxSpaceDecision.Insert(OxSpaceSource.GeometricGap, 0.85f);
            }
        }

        // Default: PDF encoded no clear boundary (§9.10) and it cannot be recovered;
        // requiring consensus is what keeps justified text free of false positives.
        return OxSpaceDecision.NoSpace(OxSpaceSource.NoSpace, 1.0f);
    }

    /// <summary>
    /// True when the boundary already carries whitespace (text.rs:1681), which is what
    /// keeps a producer's own space from being doubled.
    /// </summary>
    internal static bool HasBoundarySpace(string preceding, string following)
    {
        // Upstream matches on the ends rather than iterating, because `preceding` is the
        // whole accumulated merge text and an O(n) walk here makes the merge loop O(n^2).
        bool hasTrailingSpace = LastRune(preceding) is Rune last && Rune.IsWhiteSpace(last);
        bool hasLeadingSpace = FirstRune(following) is Rune first && Rune.IsWhiteSpace(first);
        return hasTrailingSpace || hasLeadingSpace;
    }

    /// <summary>
    /// Build the two-glyph window WordBoundaryDetector analyses at a span boundary
    /// (text.rs:1701): the last scalar of the preceding text and the first of the following.
    /// </summary>
    internal static (List<CharacterInfo> Characters, BoundaryContext Context) BuildBoundaryCharacters(
        string prevText,
        string nextText,
        OxRect prevBbox,
        OxRect nextBbox,
        float fontSize,
        bool tjOffsetTriggered)
    {
        int prevLastChar = LastRune(prevText)?.Value ?? ' ';
        int nextFirstChar = FirstRune(nextText)?.Value ?? ' ';

        // Widths are estimated by spreading the bbox over the character count, and upstream
        // deliberately uses the UTF-8 byte length as an O(1) stand-in for that count —
        // exact for ASCII, close enough elsewhere — because the preceding text is the whole
        // accumulated merge buffer.
        float prevCharCount = Math.Max(Utf8Length(prevText), 1);
        float prevCharWidth = prevBbox.Width / prevCharCount;
        float prevLastX = prevBbox.X + prevBbox.Width - prevCharWidth;

        float nextCharCount = Math.Max(Utf8Length(nextText), 1);
        float nextCharWidth = nextBbox.Width / nextCharCount;

        var characters = new List<CharacterInfo>
        {
            new()
            {
                Code = prevLastChar,
                GlyphId = null,
                Width = prevCharWidth,
                XPosition = prevLastX,
                // The caller has already reduced the TJ array to a yes/no, so the detector is
                // handed a synthetic offset well past any threshold to stand for "yes".
                TjOffset = tjOffsetTriggered ? -200 : null,
                FontSize = fontSize,
                IsLigature = false,
                OriginalLigature = null,
                ProtectedFromSplit = false,
            },
            new()
            {
                Code = nextFirstChar,
                GlyphId = null,
                Width = nextCharWidth,
                XPosition = nextBbox.X,
                TjOffset = null,
                FontSize = fontSize,
                IsLigature = false,
                OriginalLigature = null,
                ProtectedFromSplit = false,
            },
        };

        // Tz/Tw/Tc are per-glyph text state and no longer available once spans exist, so the
        // context carries the defaults.
        var context = new BoundaryContext(fontSize)
        {
            HorizontalScaling = 100.0f,
            WordSpacing = 0.0f,
            CharSpacing = 0.0f,
        };

        return (characters, context);
    }

    /// <summary>
    /// True when the text either side of the boundary looks like an email address
    /// (text.rs:1769) — "user@outlook" + ".com", or "user@" + "domain.com".
    /// </summary>
    internal static bool IsEmailContext(string precedingText, string followingText)
    {
        // Only the last 64 bytes are examined: the preceding text is the accumulated merge
        // buffer, and scanning all of it would make the merge loop quadratic.
        string prev = LastUtf8Bytes(precedingText, 64).TrimEnd();
        string next = followingText.TrimStart();

        if (prev.Contains('@'))
        {
            string afterAt = prev[(prev.LastIndexOf('@') + 1)..];

            // "outlook" + "." — the dot before the TLD was split off.
            if (afterAt.Length > 0 && next.StartsWith('.'))
            {
                return true;
            }

            // "outlook." + "com" — the TLD itself was split off.
            if (afterAt.EndsWith('.') && FirstRune(next) is Rune tld && IsAsciiAlphabetic(tld))
            {
                return true;
            }
        }

        // The split fell immediately after the '@'.
        return prev.EndsWith('@')
            && FirstRune(next) is Rune domain && IsAsciiAlphanumeric(domain);
    }

    /// <summary>
    /// True when the font sizes and bbox positions read as a superscript citation marker
    /// (text.rs:1818, §9.3): markers run 50-75% of body size and sit raised off the baseline.
    /// </summary>
    internal static bool IsCitationContext(
        OxRect? prevBbox,
        OxRect? nextBbox,
        float currentFontSize,
        float prevFontSize,
        float nextFontSize)
    {
        const float SuperscriptMin = 0.5f;
        const float SuperscriptMax = 0.75f;

        float prevRatio = prevFontSize / currentFontSize;
        float nextRatio = nextFontSize / currentFontSize;

        bool prevIsSuperscript = prevRatio >= SuperscriptMin && prevRatio <= SuperscriptMax;
        bool nextIsSuperscript = nextRatio >= SuperscriptMin && nextRatio <= SuperscriptMax;

        if (prevBbox is OxRect prevBox && nextBbox is OxRect nextBox)
        {
            bool isRaised = MathF.Abs(prevBox.Y - nextBox.Y) > currentFontSize * 0.2f;
            if ((prevIsSuperscript || nextIsSuperscript) && isRaised)
            {
                return true;
            }
        }

        // Without boxes the size ratio is the only evidence left.
        return prevIsSuperscript || nextIsSuperscript;
    }

    /// <summary>Rust's <c>char::is_alphabetic</c>: the Alphabetic property, letters plus Nl.</summary>
    private static bool IsAlphabetic(Rune r) =>
        Rune.IsLetter(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.LetterNumber;

    private static bool IsAsciiDigit(Rune r) => r.Value >= '0' && r.Value <= '9';

    private static bool IsAsciiAlphabetic(Rune r) =>
        (r.Value >= 'a' && r.Value <= 'z') || (r.Value >= 'A' && r.Value <= 'Z');

    private static bool IsAsciiAlphanumeric(Rune r) => IsAsciiDigit(r) || IsAsciiAlphabetic(r);

    private static Rune? FirstRune(string text)
    {
        foreach (Rune r in text.EnumerateRunes())
        {
            return r;
        }
        return null;
    }

    private static Rune? LastRune(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }
        int start = text.Length - 1;
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
        {
            start--;
        }
        return Rune.TryGetRuneAt(text, start, out Rune rune) ? rune : Rune.ReplacementChar;
    }

    private static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// The longest suffix of <paramref name="text"/> that fits in <paramref name="maxBytes"/>
    /// UTF-8 bytes — upstream slices the last N bytes and rounds the start up to the next
    /// character boundary, which selects exactly that window.
    /// </summary>
    private static string LastUtf8Bytes(string text, int maxBytes)
    {
        if (Utf8Length(text) <= maxBytes)
        {
            return text;
        }

        int bytes = 0;
        int start = text.Length;
        int i = text.Length;
        while (i > 0)
        {
            int step = i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]) ? 2 : 1;
            int width = Rune.TryGetRuneAt(text, i - step, out Rune rune) ? rune.Utf8SequenceLength : 3;
            if (bytes + width > maxBytes)
            {
                break;
            }
            bytes += width;
            i -= step;
            start = i;
        }
        return text[start..];
    }
}
