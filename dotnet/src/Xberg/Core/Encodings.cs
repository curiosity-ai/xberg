using System.Runtime.CompilerServices;
using System.Text;

namespace Xberg.Core;

/// <summary>
/// Registers the legacy code-page encoding provider once, at module load, so that
/// charset labels the Rust engine relies on via `encoding_rs` — Shift-JIS / Windows-31J
/// (code page 932), the windows-125x family, iso-8859-x, gbk/gb18030, big5, euc-kr, ... —
/// resolve through <see cref="Encoding.GetEncoding(string)"/>.
///
/// .NET does NOT ship these code pages in the default provider; without this registration
/// <c>Encoding.GetEncoding("shift_jis")</c> throws and legacy-charset content decodes to
/// garbage. The <see cref="ModuleInitializerAttribute"/> guarantees the provider is active
/// before any extractor runs, independent of which type is touched first.
/// </summary>
internal static class Encodings
{
    private static int _registered;

    [ModuleInitializer]
    internal static void Register()
    {
        // Idempotent: RegisterProvider replaces the provider, but avoid redundant calls.
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// The encoding for a Windows code-page number (`text/windows_codepage.rs`).
    /// </summary>
    /// <remarks>
    /// The Mac script code pages are remapped: the CJK ones to their Windows equivalents, and the
    /// rest to the nearest Windows code page for the same script, because the WHATWG encoding set
    /// upstream draws on has no entry for them. An unknown number falls back to Windows-1252,
    /// which is what RTF assumes when it says nothing.
    /// </remarks>
    internal static Encoding ForWindowsCodepage(uint codepage)
    {
        uint mapped = codepage switch
        {
            10001 => 932,
            10002 => 950,
            10003 => 949,
            10008 => 936,
            10007 => 10017,
            10004 => 1256,
            10005 => 1255,
            10006 => 1253,
            10021 => 874,
            10029 => 1250,
            10081 => 1254,
            _ => codepage,
        };
        try
        {
            return Encoding.GetEncoding((int)mapped);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252);
        }
    }
}
