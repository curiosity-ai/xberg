using Xunit;

namespace Xberg.Tests;

/// <summary>
/// The font seams are process-wide statics, so any test that installs or clears them must
/// not run beside another that reads them. Everything touching <c>OxFontSeams</c> shares
/// this collection, which xUnit runs serially.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OxFontSeamCollection
{
    internal const string Name = "OxFontSeams";
}
