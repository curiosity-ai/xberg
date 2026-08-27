//! Regression test for a reachable panic in PPTX image path resolution.
//!
//! `image_handling::get_full_image_path` guarded a "starts with `..`" relationship
//! `Target` and then sliced the string at byte offset 3 to strip an assumed `"../"`
//! prefix. The guard only proves the string starts with the two ASCII bytes `..`;
//! it does not prove a third byte exists, nor that byte 3 lands on a UTF-8 char
//! boundary. Two attacker-controlled shapes reach the slice and panic:
//!
//! - `Target=".."` -> the string is exactly 2 bytes long, so `s[3..]` is out of
//!   bounds ("byte index 3 is out of bounds").
//! - `Target="..é"` -> `é` is a 2-byte UTF-8 sequence, so the string is 4 bytes
//!   long but byte index 3 falls inside `é`'s encoding ("byte index 3 is not a
//!   char boundary").
//!
//! `Target` is a verbatim, unfiltered XML attribute read from a relationship part
//! inside the PPTX ZIP (`pptx/parser.rs::parse_slide_rels`), so a crafted `.pptx`
//! fully controls this value. This is reachable from a direct, non-HTTP,
//! non-FFI embedder of the crate, where no panic-catching boundary exists, so a
//! single malformed relationship aborts (or previously would have aborted) the
//! calling process.

// ~keep: test/bench binaries print by design; org logging policy exempts tests
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)]

mod helpers;
use helpers::extract_bytes_document_blocking;

use std::io::Write;
use std::panic::{self, AssertUnwindSafe};
use xberg::{ExtractionConfig, ImageExtractionConfig};
use zip::write::{SimpleFileOptions, ZipWriter};

const POWERPOINT_MIME_TYPE: &str = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

/// Build a minimal, otherwise well-formed, single-slide `.pptx` whose slide
/// relationship file declares one image relationship with `Target` set to
/// `image_target` verbatim.
///
/// The slide's own XML body does not need to reference the image: `get_slide_images`
/// (crates/xberg/src/extraction/pptx/container.rs) iterates every relationship in
/// `slide.images`, which comes straight from the rels file, and resolves each one
/// through `get_full_image_path` before anything checks whether the slide XML
/// actually uses it. So a bare, unreferenced malformed relationship is enough to
/// reach the bug.
fn build_pptx_with_image_target(image_target: &str) -> Vec<u8> {
    let mut buffer = Vec::new();
    {
        let mut zip = ZipWriter::new(std::io::Cursor::new(&mut buffer));
        let options = SimpleFileOptions::default();

        zip.start_file("[Content_Types].xml", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
    <Default Extension="xml" ContentType="application/xml"/>
    <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
    <Default Extension="png" ContentType="image/png"/>
</Types>"#,
        )
        .unwrap();

        zip.start_file("ppt/presentation.xml", options).unwrap();
        zip.write_all(b"<?xml version=\"1.0\"?><presentation/>").unwrap();

        zip.start_file("ppt/_rels/presentation.xml.rels", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
</Relationships>"#,
        )
        .unwrap();

        zip.start_file("ppt/slides/slide1.xml", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
       xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
    <p:cSld>
        <p:spTree/>
    </p:cSld>
</p:sld>"#,
        )
        .unwrap();

        // The malformed relationship: an image-type Target that is attacker
        // controlled and inserted with no filtering (pptx/parser.rs:840-842).
        let rels_xml = format!(
            r#"<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="{}"/>
</Relationships>"#,
            image_target
        );
        zip.start_file("ppt/slides/_rels/slide1.xml.rels", options).unwrap();
        zip.write_all(rels_xml.as_bytes()).unwrap();

        let _ = zip.finish().unwrap();
    }
    buffer
}

fn image_extraction_config() -> ExtractionConfig {
    ExtractionConfig {
        use_cache: false,
        images: Some(ImageExtractionConfig {
            extract_images: true,
            ..Default::default()
        }),
        ..Default::default()
    }
}

/// A crafted PPTX whose slide relationship declares `Target=".."` must not
/// unwind the calling thread. Before the fix, `get_full_image_path` sliced the
/// 2-byte string `".."` at byte offset 3 and panicked with:
///
///   byte index 3 is out of bounds of `..`, which contains 2 bytes
///
/// Extraction must instead complete (possibly with a warning) or return a
/// structured `Err`, matching the "malformed documents must fail gracefully,
/// never panic" rule.
#[test]
fn test_bare_dotdot_target_does_not_panic() {
    let pptx_bytes = build_pptx_with_image_target("..");
    let config = image_extraction_config();

    let outcome = panic::catch_unwind(AssertUnwindSafe(|| {
        extract_bytes_document_blocking(&pptx_bytes, POWERPOINT_MIME_TYPE, &config)
    }));

    assert!(
        outcome.is_ok(),
        "extraction panicked on Target=\"..\" instead of failing gracefully"
    );
}

/// A crafted PPTX whose slide relationship declares `Target="..é"` (the `é` is a
/// 2-byte UTF-8 sequence) must not unwind the calling thread. Before the fix,
/// slicing at byte offset 3 landed inside `é`'s encoding and panicked with:
///
///   byte index 3 is not a char boundary; it is inside 'é' (bytes 2..4) of `..é`
#[test]
fn test_multibyte_char_after_dotdot_does_not_panic() {
    let pptx_bytes = build_pptx_with_image_target("..\u{e9}");
    let config = image_extraction_config();

    let outcome = panic::catch_unwind(AssertUnwindSafe(|| {
        extract_bytes_document_blocking(&pptx_bytes, POWERPOINT_MIME_TYPE, &config)
    }));

    assert!(
        outcome.is_ok(),
        "extraction panicked on Target=\"..\\u{{e9}}\" instead of failing gracefully"
    );
}

/// Positive control: the spec-compliant `"../media/imageN.png"` relationship
/// target (a slide's images live one directory above `ppt/slides/`) must keep
/// resolving to the image bytes exactly as before. This is a crash fix, not a
/// tightening: legal relationships must still extract their image data.
#[test]
fn test_well_formed_parent_relative_target_still_resolves() {
    let mut buffer = Vec::new();
    {
        let mut zip = ZipWriter::new(std::io::Cursor::new(&mut buffer));
        let options = SimpleFileOptions::default();

        zip.start_file("[Content_Types].xml", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
    <Default Extension="xml" ContentType="application/xml"/>
    <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
    <Default Extension="png" ContentType="image/png"/>
</Types>"#,
        )
        .unwrap();

        zip.start_file("ppt/presentation.xml", options).unwrap();
        zip.write_all(b"<?xml version=\"1.0\"?><presentation/>").unwrap();

        zip.start_file("ppt/_rels/presentation.xml.rels", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
</Relationships>"#,
        )
        .unwrap();

        // The slide XML must reference the relationship via a real `<p:pic>` /
        // `r:embed` pair: the outer extraction loop only keeps an image that
        // both resolved via the rels file AND appears as a `SlideElement::Image`
        // parsed from the slide body (crates/xberg/src/extraction/pptx/mod.rs).
        zip.start_file("ppt/slides/slide1.xml", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<p:sld xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
       xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
    <p:cSld>
        <p:spTree>
            <p:pic>
                <p:nvPicPr>
                    <p:cNvPr id="2" name="Picture 1"/>
                    <p:cNvPicPr/>
                    <p:nvPr/>
                </p:nvPicPr>
                <p:blipFill>
                    <a:blip r:embed="rId2"/>
                    <a:stretch><a:fillRect/></a:stretch>
                </p:blipFill>
                <p:spPr>
                    <a:xfrm><a:off x="0" y="0"/><a:ext cx="100000" cy="100000"/></a:xfrm>
                    <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                </p:spPr>
            </p:pic>
        </p:spTree>
    </p:cSld>
</p:sld>"#,
        )
        .unwrap();

        zip.start_file("ppt/slides/_rels/slide1.xml.rels", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
</Relationships>"#,
        )
        .unwrap();

        // The image lives at ppt/media/image1.png: one directory up from
        // ppt/slides/ (where the slide's rels resolve "..") plus "media/image1.png".
        zip.start_file("ppt/media/image1.png", options).unwrap();
        // Minimal 1x1 PNG signature + IHDR/IDAT/IEND is unnecessary here: only
        // byte presence/round-tripping through the archive is under test, not
        // image decoding.
        zip.write_all(&[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A])
            .unwrap();

        let _ = zip.finish().unwrap();
    }

    let config = image_extraction_config();
    let result = extract_bytes_document_blocking(&buffer, POWERPOINT_MIME_TYPE, &config)
        .expect("well-formed parent-relative image relationship must extract successfully");

    let images = result
        .images
        .as_ref()
        .expect("extraction with extract_images=true must populate the images field");

    assert_eq!(
        images.len(),
        1,
        "expected exactly one resolved image from the ../media/image1.png relationship"
    );
    assert_eq!(
        images[0].data.as_ref(),
        [0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A].as_slice(),
        "image bytes read via the resolved ppt/media/image1.png path must match what was archived"
    );
}
