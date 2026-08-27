# Third-Party Notices — .NET port

Xberg is licensed under [MIT](../LICENSE). The C# files listed below are
**derivative works of Apache-2.0 licensed Rust crates**, translated to C#. They
remain subject to the Apache License 2.0, a copy of which is in
[`third_party/LICENSE-Apache-2.0.txt`](third_party/LICENSE-Apache-2.0.txt).

This is what Apache-2.0 §2 permits and §4 requires: the license text is
included, each derived file carries a notice naming its source and stating that
it was modified, and the upstream attribution is retained. None of the three
crates ships a `NOTICE` file, so §4(d) does not apply.

> **Note for packagers.** Every other Rust crate the .NET port derives from is
> MIT or dual `MIT OR Apache-2.0` (where MIT can be taken), so the assembly was
> previously MIT throughout. These three are Apache-2.0 *only*, so
> `<Packagelicense>MIT</Packagelicense>` in `src/Xberg/Xberg.csproj` no longer
> describes the whole assembly. A combined expression such as
> `MIT AND Apache-2.0` would. That declaration has been left as it is, because
> changing what a published package tells its consumers is the maintainer's
> call, not this port's.

## typst-syntax 0.15.1 — Apache-2.0

- Copyright: The Typst Project Developers
- Source: <https://github.com/typst/typst>
- Derived files: `src/Xberg/Internal/Math/TypstKind.cs`,
  `src/Xberg/Internal/Math/TypstLexer.cs`,
  `src/Xberg/Internal/Math/TypstNode.cs`,
  `src/Xberg/Internal/Math/TypstParser.cs`,
  and the `default_math_class` overrides in
  `src/Xberg/Internal/Math/TypstMathClass.cs` (from `typst-utils` 0.15.1, also
  Apache-2.0 and also by The Typst Project Developers)
- Modifications: the math-mode slice of the crate's lexer, syntax tree, and
  parser translated to C#. Markup mode, spans, the newline modes, incremental
  reparsing, memoization and diagnostics are omitted; only what `parse_math`
  reaches is ported. Code mode, which math enters at a `#`, is reduced to the
  shapes a `#` takes inside math — literals, names, field accesses, calls with
  named and spread arguments, bracketed groups, let bindings, set and show
  rules, and closures — with other keywords consumed rather than modelled.
  Validated at 486 of 487 trees identical to the crate's own parser over every
  `$…$` span in the corpus, the last being a documentation placeholder that
  renders the same either way.

## mathemascii 0.4.0 — Apache-2.0

- Copyright: Nadir Fejzic
- Source: <https://github.com/nfejzic/mathemascii>
- Derived files: `src/Xberg/Internal/Math/AsciiMathLexer.cs`,
  `src/Xberg/Internal/Math/AsciiMath.cs`,
  `src/Xberg/Internal/Math/AsciiMathSymbols.cs`
- Modifications: the scanner, lexer, parser, and AST translated to C#. The
  crate's panics on multi-byte input and on `cancel` are raised as an exception
  rather than aborting, so the caller can drop the equation instead of the
  process.

## alemat 0.8.0 — Apache-2.0

- Copyright: Nadir Fejzic
- Source: <https://github.com/nfejzic/alemat>
- Derived files: `src/Xberg/Internal/Math/AsciiMath.cs` (the MathML element
  tree and its writer), `src/Xberg/Internal/Math/AsciiMathSymbols.cs` (symbol
  values resolved through the crate's `Ident`/`Operator` dictionaries)
- Modifications: only the element kinds `mathemascii` builds are ported, with
  the builder and writer behaviour of `BufMathMlWriter` reproduced; the crate's
  type-state builders, renderer trait, and unused elements are omitted.
