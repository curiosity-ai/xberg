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
}
