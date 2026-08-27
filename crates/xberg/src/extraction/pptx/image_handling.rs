//! Image parsing and format detection.
//!
//! This module handles image-related parsing from slide XML and
//! detection of image formats from file data.

use std::borrow::Cow;

pub(super) fn detect_image_format(data: &[u8]) -> Cow<'static, str> {
    crate::extraction::image_format::detect_image_format(data)
}

pub(super) fn get_slide_rels_path(slide_path: &str) -> String {
    let parts: Vec<&str> = slide_path.rsplitn(2, '/').collect();
    if parts.len() == 2 {
        format!("{}/_rels/{}.rels", parts[1], parts[0])
    } else {
        format!("_rels/{}.rels", slide_path)
    }
}

pub(super) fn get_full_image_path(slide_path: &str, image_target: &str) -> String {
    // OPC relationship targets resolve relative to the directory containing the *source*
    // part -- the slide XML file itself, not one of its ancestors. Every known slide path is
    // "ppt/slides/slideN.xml", so "ppt/slides" is the fallback when `slide_path` carries no
    // directory component at all (e.g. a synthetic or malformed part name).
    let base = slide_path.rsplit_once('/').map_or("ppt/slides", |(dir, _)| dir);

    // A target that cannot be safely resolved (pops past the package root, carries a NUL
    // byte, or a drive/UNC prefix) has no legitimate resolution. This value only ever
    // feeds an in-memory `ZipArchive::by_name` lookup (`extraction/pptx/container.rs`),
    // and no ZIP archive can contain an entry with an empty name, so returning an empty
    // string is a safe sentinel: the lookup is guaranteed to miss rather than resolve to
    // something unintended. ~keep
    crate::extractors::security::resolve_container_entry(base, image_target).unwrap_or_default()
}

#[cfg(test)]
mod panic_regression_tests {
    //! `get_full_image_path` used to guard on `starts_with("..")` and then slice the string
    //! at the fixed byte offset 3 to strip an assumed `"../"` prefix -- a reachable panic,
    //! since `Target` is a verbatim, unfiltered XML attribute from a PPTX relationship part
    //! (`pptx/parser.rs::parse_slide_rels`) that a crafted `.pptx` fully controls. An earlier
    //! fix replaced the slice with `strip_prefix("../")`, which cannot panic but also does no
    //! validation at all.
    //!
    //! This resolution now goes through the shared `resolve_container_entry` (see
    //! `extractors::security`), which walks `/`-delimited segments by exact string
    //! comparison and never indexes into the middle of a string, so the panic class is gone
    //! structurally rather than patched at one call site. It also gains real traversal
    //! validation PPTX never had: a target that pops past the package root is now rejected
    //! instead of producing an unchecked, doomed `by_name` lookup. Two of the cases below
    //! (`bare_dotdot_*`, `backslash_variant_*`) resolve to a different value than the
    //! post-panic-fix code did; both changes are pinned and explained below, and neither
    //! affects a well-formed presentation.
    use super::get_full_image_path;

    /// Bare `".."` against the normal two-level slide directory pops one level and stops --
    /// there is no filename left, so the result is the parent directory itself ("ppt"), not a
    /// file. The panic-fix version treated a bare ".." as a literal filename
    /// ("ppt/slides/.."); both are non-existent ZIP entries, so this is inert for archive
    /// lookups but is the more honest resolution.
    #[test]
    fn bare_dotdot_resolves_to_the_parent_directory_not_a_literal_filename() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "..");
        assert_eq!(result, "ppt");
    }

    /// `Target="..\u{e9}"` (`é` is a 2-byte UTF-8 sequence) is not the exact string `".."`, so
    /// it is pushed as an ordinary literal path segment -- confirming the panic class (byte
    /// index 3 landing inside `é`'s encoding) cannot recur, since resolution never indexes by
    /// byte offset at all.
    #[test]
    fn should_not_panic_on_multibyte_char_after_dotdot() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "..\u{e9}");
        assert_eq!(result, "ppt/slides/..\u{e9}");
    }

    /// Same bare-".." case with no slide directory at all: falls back to the "ppt/slides"
    /// default base, then pops one level to "ppt".
    #[test]
    fn bare_dotdot_without_slide_directory_uses_the_default_base() {
        let result = get_full_image_path("slide1.xml", "..");
        assert_eq!(result, "ppt");
    }

    /// Deliberate behaviour change: a backslash-separated target is now normalised the same
    /// as its forward-slash form (`resolve_container_entry` converts `\` to `/` explicitly)
    /// instead of being treated as one opaque literal filename. This removes the
    /// platform-dependent drift that motivated deleting `has_path_traversal` -- Windows-style
    /// and Unix-style relationship targets now resolve identically regardless of build
    /// platform.
    #[test]
    fn backslash_variant_now_resolves_the_same_as_the_forward_slash_form() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "..\\media\\image1.png");
        assert_eq!(result, "ppt/media/image1.png");
    }

    /// Positive control: the spec-compliant `"../media/image1.png"` relationship
    /// target must keep resolving exactly as before the fix. This is a crash
    /// fix, not a tightening — legal relationships must not regress.
    #[test]
    fn well_formed_parent_relative_target_resolves_unchanged() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "../media/image1.png");
        assert_eq!(result, "ppt/media/image1.png");
    }

    /// Positive control: a same-directory target (no ".." prefix at all) must
    /// keep resolving exactly as before the fix.
    #[test]
    fn direct_target_resolves_unchanged() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "image1.png");
        assert_eq!(result, "ppt/slides/image1.png");
    }

    /// New coverage the migration brings that the string-mangling implementation never had:
    /// a target that pops past the package root is rejected (empty-string sentinel) instead
    /// of producing an unchecked, doomed `by_name` lookup. This is the real win of the
    /// migration -- PPTX previously had zero traversal validation on this path.
    #[test]
    fn out_of_bounds_traversal_is_now_rejected_instead_of_unchecked() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "../../../../etc/passwd");
        assert_eq!(result, "");
    }

    /// A Windows drive letter is rejected outright rather than treated as a literal filename.
    #[test]
    fn drive_letter_target_is_rejected() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "C:\\evil.png");
        assert_eq!(result, "");
    }

    /// A NUL byte can never appear in a real ZIP entry name; rejected outright rather than
    /// reaching a doomed `by_name` lookup.
    #[test]
    fn nul_byte_target_is_rejected() {
        let result = get_full_image_path("ppt/slides/slide1.xml", "media/\0image1.png");
        assert_eq!(result, "");
    }
}
