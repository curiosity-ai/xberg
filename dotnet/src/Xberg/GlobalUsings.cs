// The domain enum Xberg.Types.UriKind (Hyperlink/Anchor/Email/…) collides with
// System.UriKind under ImplicitUsings. No library code uses System.UriKind, so alias
// the bare name to the domain type globally; qualify System.UriKind explicitly if ever needed.
global using UriKind = Xberg.Types.UriKind;
