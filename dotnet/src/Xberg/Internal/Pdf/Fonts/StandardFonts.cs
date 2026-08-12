// Adobe Core-14 AFM glyph advance widths (1000ths of em), ported verbatim
// from pdf_oxide fonts/font_dict.rs get_standard_font_width. Used when a
// Standard-14 font has no /Widths entry for a code (notably the space glyph
// below FirstChar), mirroring pdf_oxide's width source exactly.
namespace Xberg.Internal.Pdf.Fonts;

internal static class StandardFonts
{
    private static readonly System.Collections.Generic.Dictionary<int,double> TimesBoldItalic = new() { [32]=250, [33]=389, [34]=555, [35]=500, [36]=500, [37]=833, [38]=778, [39]=333, [40]=333, [41]=333, [42]=500, [43]=570, [44]=250, [45]=333, [46]=250, [47]=278, [48]=500, [49]=500, [50]=500, [51]=500, [52]=500, [53]=500, [54]=500, [55]=500, [56]=500, [57]=500, [58]=333, [59]=333, [60]=570, [61]=570, [62]=570, [63]=500, [64]=832, [65]=667, [66]=667, [67]=667, [68]=722, [69]=667, [70]=667, [71]=722, [72]=778, [73]=389, [74]=500, [75]=667, [76]=611, [77]=889, [78]=722, [79]=722, [80]=611, [81]=722, [82]=667, [83]=556, [84]=611, [85]=722, [86]=667, [87]=889, [88]=667, [89]=611, [90]=611, [91]=333, [92]=278, [93]=333, [94]=570, [95]=500, [97]=500, [98]=500, [99]=444, [100]=500, [101]=444, [102]=333, [103]=500, [104]=556, [105]=278, [106]=278, [107]=500, [108]=278, [109]=778, [110]=556, [111]=500, [112]=500, [113]=500, [114]=389, [115]=389, [116]=278, [117]=556, [118]=444, [119]=667, [120]=500, [121]=444, [122]=389 };
    private static readonly System.Collections.Generic.Dictionary<int,double> TimesBold = new() { [32]=250, [33]=333, [34]=555, [35]=500, [36]=500, [37]=1000, [38]=833, [39]=333, [40]=333, [41]=333, [42]=500, [43]=570, [44]=250, [45]=333, [46]=250, [47]=278, [48]=500, [49]=500, [50]=500, [51]=500, [52]=500, [53]=500, [54]=500, [55]=500, [56]=500, [57]=500, [58]=333, [59]=333, [60]=570, [61]=570, [62]=570, [63]=500, [64]=930, [65]=722, [66]=667, [67]=722, [68]=722, [69]=667, [70]=611, [71]=778, [72]=778, [73]=389, [74]=500, [75]=778, [76]=667, [77]=944, [78]=722, [79]=778, [80]=611, [81]=778, [82]=722, [83]=556, [84]=667, [85]=722, [86]=722, [87]=1000, [88]=722, [89]=722, [90]=667, [91]=333, [92]=278, [93]=333, [94]=581, [95]=500, [97]=500, [98]=556, [99]=444, [100]=556, [101]=444, [102]=333, [103]=500, [104]=556, [105]=278, [106]=333, [107]=556, [108]=278, [109]=833, [110]=556, [111]=500, [112]=556, [113]=556, [114]=444, [115]=389, [116]=333, [117]=556, [118]=500, [119]=722, [120]=500, [121]=500, [122]=444 };
    private static readonly System.Collections.Generic.Dictionary<int,double> TimesItalic = new() { [32]=250, [33]=333, [34]=420, [35]=500, [36]=500, [37]=833, [38]=778, [39]=333, [40]=333, [41]=333, [42]=500, [43]=675, [44]=250, [45]=333, [46]=250, [47]=278, [48]=500, [49]=500, [50]=500, [51]=500, [52]=500, [53]=500, [54]=500, [55]=500, [56]=500, [57]=500, [58]=333, [59]=333, [60]=675, [61]=675, [62]=675, [63]=500, [64]=920, [65]=611, [66]=611, [67]=667, [68]=722, [69]=611, [70]=611, [71]=722, [72]=722, [73]=333, [74]=444, [75]=667, [76]=556, [77]=833, [78]=667, [79]=722, [80]=611, [81]=722, [82]=611, [83]=500, [84]=556, [85]=722, [86]=611, [87]=833, [88]=611, [89]=556, [90]=556, [91]=389, [92]=278, [93]=389, [94]=422, [95]=500, [97]=500, [98]=500, [99]=444, [100]=500, [101]=444, [102]=278, [103]=500, [104]=500, [105]=278, [106]=278, [107]=444, [108]=278, [109]=722, [110]=500, [111]=500, [112]=500, [113]=500, [114]=389, [115]=389, [116]=278, [117]=500, [118]=444, [119]=667, [120]=444, [121]=444, [122]=389 };
    private static readonly System.Collections.Generic.Dictionary<int,double> TimesRoman = new() { [32]=250, [33]=333, [34]=408, [35]=500, [36]=500, [37]=833, [38]=778, [39]=333, [40]=333, [41]=333, [42]=500, [43]=564, [44]=250, [45]=333, [46]=250, [47]=278, [48]=500, [49]=500, [50]=500, [51]=500, [52]=500, [53]=500, [54]=500, [55]=500, [56]=500, [57]=500, [58]=278, [59]=278, [60]=564, [61]=564, [62]=564, [63]=444, [64]=921, [65]=722, [66]=667, [67]=667, [68]=722, [69]=611, [70]=556, [71]=722, [72]=722, [73]=333, [74]=389, [75]=722, [76]=611, [77]=889, [78]=722, [79]=722, [80]=556, [81]=722, [82]=667, [83]=556, [84]=611, [85]=722, [86]=722, [87]=944, [88]=722, [89]=722, [90]=611, [91]=333, [92]=278, [93]=333, [97]=444, [98]=500, [99]=444, [100]=500, [101]=444, [102]=333, [103]=500, [104]=500, [105]=278, [106]=278, [107]=500, [108]=278, [109]=778, [110]=500, [111]=500, [112]=500, [113]=500, [114]=333, [115]=389, [116]=278, [117]=500, [118]=500, [119]=722, [120]=500, [121]=500, [122]=444 };
    private static readonly System.Collections.Generic.Dictionary<int,double> HelveticaBold = new() { [32]=278, [33]=333, [34]=474, [44]=278, [45]=333, [46]=278, [47]=278, [48]=556, [49]=556, [50]=556, [51]=556, [52]=556, [53]=556, [54]=556, [55]=556, [56]=556, [57]=556, [58]=333, [59]=333, [65]=722, [66]=722, [67]=722, [68]=722, [69]=667, [70]=611, [71]=778, [72]=722, [73]=278, [74]=556, [75]=722, [76]=611, [77]=833, [78]=722, [79]=778, [80]=667, [81]=778, [82]=722, [83]=667, [84]=611, [85]=722, [86]=667, [87]=944, [88]=667, [89]=667, [90]=611, [97]=556, [98]=611, [99]=556, [100]=611, [101]=556, [102]=333, [103]=611, [104]=611, [105]=278, [106]=278, [107]=556, [108]=278, [109]=889, [110]=611, [111]=611, [112]=611, [113]=611, [114]=389, [115]=556, [116]=333, [117]=611, [118]=556, [119]=778, [120]=556, [121]=556, [122]=500 };
    private static readonly System.Collections.Generic.Dictionary<int,double> Helvetica = new() { [32]=278, [33]=278, [34]=355, [44]=278, [45]=333, [46]=278, [47]=278, [48]=556, [49]=556, [50]=556, [51]=556, [52]=556, [53]=556, [54]=556, [55]=556, [56]=556, [57]=556, [58]=278, [59]=278, [65]=667, [66]=667, [67]=722, [68]=722, [69]=667, [70]=611, [71]=778, [72]=722, [73]=278, [74]=500, [75]=667, [76]=556, [77]=833, [78]=722, [79]=778, [80]=667, [81]=778, [82]=722, [83]=667, [84]=611, [85]=722, [86]=667, [87]=944, [88]=667, [89]=667, [90]=611, [97]=556, [98]=556, [99]=500, [100]=556, [101]=556, [102]=278, [103]=556, [104]=556, [105]=222, [106]=222, [107]=500, [108]=222, [109]=833, [110]=556, [111]=556, [112]=556, [113]=556, [114]=333, [115]=500, [116]=278, [117]=556, [118]=500, [119]=722, [120]=500, [121]=500, [122]=444 };

    private static readonly System.Collections.Generic.HashSet<string> Std14 = new(System.StringComparer.Ordinal)
    {
        "Courier", "Courier-Bold", "Courier-BoldOblique", "Courier-Oblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-BoldOblique", "Helvetica-Oblique",
        "HelveticaOblique", "Times-Roman", "Times-Bold", "Times-BoldItalic",
        "Times-Italic", "Symbol", "ZapfDingbats",
    };

    /// <summary>Resolved per-glyph metrics for one Standard-14 font: either a width
    /// table (Times/Helvetica) or the monospace Courier flag (fixed 600).</summary>
    public sealed class Metrics
    {
        private readonly System.Collections.Generic.Dictionary<int, double>? _table;
        private readonly bool _courier;
        internal Metrics(System.Collections.Generic.Dictionary<int, double>? table, bool courier)
        { _table = table; _courier = courier; }

        public double? Width(int code)
        {
            if (_courier) return 600.0;
            return _table != null && _table.TryGetValue(code, out var w) ? w : (double?)null;
        }
    }

    /// <summary>Resolve a base-font name (subset prefix stripped) to its Standard-14
    /// metrics once at font load, or null if it is not one of the canonical 14.
    /// Mirrors the font-name dispatch in get_standard_font_width.</summary>
    public static Metrics? Resolve(string baseFont)
    {
        string name = baseFont;
        int plus = baseFont.IndexOf('+');
        if (plus >= 0 && plus + 1 < baseFont.Length) name = baseFont.Substring(plus + 1);
        if (!Std14.Contains(name)) return null;
        if (name.StartsWith("Courier", System.StringComparison.Ordinal)) return new Metrics(null, true);
        bool isBold = name.Contains("Bold");
        if (name.StartsWith("Times", System.StringComparison.Ordinal))
        {
            var t = name.Contains("BoldItalic") ? TimesBoldItalic
                : isBold ? TimesBold
                : name.Contains("Italic") ? TimesItalic
                : TimesRoman;
            return new Metrics(t, false);
        }
        if (name.StartsWith("Helvetica", System.StringComparison.Ordinal))
            return new Metrics(isBold ? HelveticaBold : Helvetica, false);
        return null; // Symbol / ZapfDingbats: no ASCII metric table
    }
}
