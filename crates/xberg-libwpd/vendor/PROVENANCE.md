# Vendored sources: provenance

This directory vendors the upstream C++ sources this crate builds from, as
**compressed `.tar.gz` archives committed in-tree**. `build.rs` extracts all
three archives into `OUT_DIR` at build time (sha256-verifying the two upstream
archives against constants it carries), then compiles the result via the `cc`
crate. `cargo build -p xberg-libwpd` never needs network access or a system
boost install.

## librevenge 0.0.6

- Archive: `vendor/librevenge-0.0.6.tar.gz`
- Source: `https://downloads.sourceforge.net/project/libwpd/librevenge/librevenge-0.0.6/librevenge-0.0.6.tar.gz`
  (mirror: `https://netcologne.dl.sourceforge.net/project/libwpd/librevenge/librevenge-0.0.6/librevenge-0.0.6.tar.gz`)
- SHA-256: `686cc36be3196a0a808761cfd3951a46ff809cb0e028b0902c787261a1389d0f`
- Verified against this pin (encoded as a constant in `build.rs`) before
  extraction, both when this archive was vendored and again on every build.
- Vendored under the MPL-2.0 arm of its dual MPL-2.0/LGPL-2.1 licensing.
  `COPYING.MPL` and `COPYING.LGPL` are both carried over unmodified from the
  tarball; the crate itself is built against MPL-2.0.
- Pruned from the upstream tarball: autotools/build-system files (`configure`,
  `Makefile.am`/`.in`, `aclocal.m4`, `m4/`, `ltmain.sh`, `config.guess`/`.sub`,
  `install-sh`, `missing`, `compile`, `depcomp`, `ar-lib`, `test-driver`,
  `autogen.sh`, `build/`), docs (`docs/`, `ChangeLog`, `HACKING`, `INSTALL`,
  `NEWS`), the `src/test/` and `src/fuzz/` trees, and `.pc.in` pkg-config
  templates. Kept in full: `inc/` (all public headers) and every file under
  `src/lib/` (all 30 `.cpp` translation units + all headers), which is exactly
  the set `build.rs` compiles/includes.

## libwpd 0.10.3

- Archive: `vendor/libwpd-0.10.3.tar.gz`
- Source: `https://downloads.sourceforge.net/project/libwpd/libwpd/libwpd-0.10.3/libwpd-0.10.3.tar.gz`
  (mirror: `https://netcologne.dl.sourceforge.net/project/libwpd/libwpd/libwpd-0.10.3/libwpd-0.10.3.tar.gz`)
- SHA-256: `ca3575282acff8c952c12160433ad7e73e803ff3f070b8442c7ffa1f3a19f9ae`
- Verified against this pin (encoded as a constant in `build.rs`) before
  extraction, both when this archive was vendored and again on every build.
- Vendored under the MPL-2.0 arm of its dual MPL-2.0/LGPL-2.1 licensing, same
  as librevenge. `COPYING.MPL` and `COPYING.LGPL` are carried over unmodified.
- Pruned the same categories of build-system/doc cruft as librevenge, plus the
  `src/conv/` command-line converter tree (not used by the shim) and
  `src/fuzz/`. Kept in full: `inc/` and every file under `src/lib/` (all 170
  `.cpp` files + all headers) — exactly what `build.rs` compiles/includes.

## boost (header subset)

- Archive: `vendor/boost-subset.tar.gz`
- SHA-256: `802ee17c5e380efbcbb696468ee3c7090aa409db89c2063b4c9b8d3e3aff1e08`
- sha256-verified by `build.rs` at extract time, same as the two upstream
  archives above. It isn't a third-party download — it's a build artifact of the
  `bcp` command below, produced and committed by us — but it's pinned anyway so
  an accidental corruption of the committed archive fails the build loudly.
- Extracting the archive reproduces the exact `vendor/boost/` layout this
  crate previously carried unzipped: a top-level `boost/` directory containing
  the `bcp` output tree (so the extracted include root is `<archive
  root>/boost`, i.e. `boost/boost/version.hpp` etc. once extracted).
- Version: 1.90.0 (Homebrew `boost` formula, `/opt/homebrew/Cellar/boost/1.90.0_1`),
  used as the `--boost=` source tree for `bcp`.
- Tool: `bcp` 1.90.0, installed via `brew install boost-bcp`.
- Commands used to compute the exact transitive closure of headers actually
  needed (three invocations, merged into the same output tree — the first pass
  per the task's suggested module list left a few headers uncovered because
  `bcp`'s `serialization`/`base64_from_binary.hpp` module args don't pull in
  every `archive::iterators` adapter header these files include directly, and
  `algorithm/string.hpp` isn't a transitive dependency of
  `spirit`/`archive`/`serialization` at all. Each gap was found empirically:
  `cargo build -p xberg-libwpd` failed on one missing header at a time
  (`boost/algorithm/string.hpp`, then
  `boost/archive/iterators/binary_from_base64.hpp`), each fixed by an
  additional targeted `bcp` pass, ending in a clean build):

  ```sh
  bcp --boost=/opt/homebrew/Cellar/boost/1.90.0_1/include \
      spirit archive/iterators/base64_from_binary.hpp serialization version.hpp \
      vendor/boost

  bcp --boost=/opt/homebrew/Cellar/boost/1.90.0_1/include \
      algorithm/string.hpp \
      vendor/boost

  bcp --boost=/opt/homebrew/Cellar/boost/1.90.0_1/include \
      archive/iterators/binary_from_base64.hpp \
      archive/iterators/remove_whitespace.hpp \
      archive/iterators/transform_width.hpp \
      vendor/boost
  ```

  (`bcp`'s output nests everything under a `boost/` directory, so the result
  landed at `vendor/boost/boost/...`.)
- Rationale for the module list: librevenge/libwpd's actual `#include
  <boost/...>` lines (verified by grepping `src/lib/` in both trees) are
  `boost/algorithm/string.hpp`, `boost/archive/iterators/base64_from_binary.hpp`,
  `boost/archive/iterators/binary_from_base64.hpp`,
  `boost/archive/iterators/remove_whitespace.hpp`,
  `boost/archive/iterators/transform_width.hpp`, and
  `boost/spirit/include/qi.hpp`. Only the header-only `archive::iterators`
  adapters are used (not full `boost::archive`/object serialization), but
  `bcp`'s `serialization`/`archive/iterators/base64_from_binary.hpp` module
  arguments pull in exactly that closure, matching what the task asked for.
  `spirit` is what pulls in the bulk of the tree (`phoenix`, `fusion`, `mpl`,
  `proto`, etc.) since `boost::spirit::qi` is a template-metaprogramming
  parser-combinator library.
- No fallback was needed: `bcp` was available after `brew install boost-bcp`.
- Result: `vendor/boost/boost/` (~61 MB, ~4500 header files), all header-only.
  Nothing under it requires a separately-compiled/linked boost library — the
  only boost usage in librevenge/libwpd is header-only (`spirit::qi`,
  `algorithm::string`, and the `archive::iterators` base64 codec adapters).
- `boost/version.hpp` (`BOOST_LIB_VERSION "1_90"`) is present, per the required
  headers this crate's build previously probed for on the system
  (`find_boost_include` in the old `build.rs`).
