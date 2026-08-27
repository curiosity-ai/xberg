using System;
using System.Linq;
using System.Text;
using Xberg.Internal.Text;
using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The "the text came out as character codes" check. A PDF whose fonts carry a broken or
/// missing /ToUnicode CMap draws correctly and extracts to well-formed Latin words that mean
/// nothing, so nothing upstream fails and only the finished text can give it away.
/// </summary>
public class UndecodedTextDetectionTests
{
    private const string EnglishProse =
        "The quick brown fox jumps over the lazy dog. Extraction quality depends on the font "
      + "resources a producer chose to embed, and on whether the character codes it wrote can be "
      + "mapped back to Unicode at all. Where a document carries a correct ToUnicode CMap the "
      + "mapping is exact; where it carries none, the extractor must fall back on the encoding "
      + "name and the glyph names inside the font programme, which is a guess that usually works "
      + "for text set in a standard Latin face and fails for a subset font whose glyph names the "
      + "producer renumbered. This paragraph exists to be long enough to score.";

    private const string GermanProse =
        "Mit dem offiziellen Spatenstich startet die Gemeinde Schutterwald ein zukunftsweisendes "
      + "Projekt, die Erweiterung des bestehenden Nahwaermenetzes und die Installation einer "
      + "neuen Hackschnitzelheizung bei der Moerburghalle. Die nachhaltige Waermeversorgung in "
      + "der Ortsmitte soll damit langfristig gesichert werden, und die Gemeinde rechnet mit "
      + "einer deutlichen Verringerung des Verbrauchs fossiler Brennstoffe im Verlauf der "
      + "naechsten Jahre. Dieser Absatz ist lang genug, um bewertet zu werden.";

    /// <summary>Shift every ASCII letter by <paramref name="by"/>, the way a subset font with a
    /// renumbered encoding and no ToUnicode comes out.</summary>
    private static string CaesarShift(string text, int by)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is >= 'a' and <= 'z') sb.Append((char)('a' + (c - 'a' + by) % 26));
            else if (c is >= 'A' and <= 'Z') sb.Append((char)('A' + (c - 'A' + by) % 26));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData(EnglishProse)]
    [InlineData(GermanProse)]
    public void RealProse_IsNotFlagged(string text) => Assert.Null(UndecodedTextDetection.Diagnose(text));

    [Fact]
    public void AShiftedAlphabet_IsFlaggedOnItsVowelStructure()
    {
        // A permutation keeps a normal letter-frequency profile, so only the vowels give it away.
        var shifted = CaesarShift(GermanProse, 3);
        var reason = UndecodedTextDetection.Diagnose(shifted);
        Assert.NotNull(reason);
        Assert.Contains("vowel structure", reason);
    }

    [Fact]
    public void ManyCodesCollapsedOntoOneLetter_IsFlaggedOnItsLetterDistribution()
    {
        // The other failure shape: a CMap that maps most codes to the same character, which
        // leaves plausible vowels but destroys the frequency profile.
        var collapsed = new string(
            EnglishProse.Select(c => char.IsLetter(c) ? (char.IsUpper(c) ? 'A' : "aab"[c % 3]) : c).ToArray());
        var reason = UndecodedTextDetection.Diagnose(collapsed);
        Assert.NotNull(reason);
        Assert.Contains("letter distribution", reason);
    }

    [Fact]
    public void ShortText_IsNeverJudged()
    {
        // A caption legitimately looks like anything, and the statistics are noise below a few
        // hundred letters.
        Assert.Null(UndecodedTextDetection.Diagnose(CaesarShift("Nachhaltige Waermeversorgung", 3)));
    }

    [Fact]
    public void NonLatinText_IsNeverJudged()
    {
        // Every check is built on an alphabet that spells words out of vowels and consonants.
        var japanese = string.Concat(Enumerable.Repeat("これは日本語のテキストです。抽出の品質はフォントに依存します。", 40));
        Assert.Null(UndecodedTextDetection.Diagnose(japanese));
    }

    [Fact]
    public void ABase64AttachmentDump_IsNotFlagged()
    {
        // The closest legitimate case in the corpus: an email body that is a base64 payload
        // reaches a 0.245 vowel ratio, but its consonant runs stay short because base64 breaks
        // into lines and carries digits throughout.
        var payload = string.Concat(Enumerable.Repeat(
            "JVBERi0xLjMNCiXi48/TDQoxIDAgb2JqDQo8PA0KL1R5cGUgL0NhdGFsb2cNCi9Pd\n", 30));
        Assert.Null(UndecodedTextDetection.Diagnose(payload));
    }

    [Fact]
    public void EmptyText_IsNotFlagged() => Assert.Null(UndecodedTextDetection.Diagnose(""));
}
