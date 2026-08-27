//! Regression test: a DOCX image relationship whose `Target` legitimately points one
//! directory above `word/` (`../media/imageN.png`) must resolve, matching the OPC
//! relationship convention. This is the documented loosening from the path-traversal
//! unification (see `crates/xberg/src/extractors/security.rs::resolve_container_entry`).
//!
//! Before the unification, `docx.rs`'s image-embedding guard was `has_path_traversal`,
//! which rejected *any* `..` component regardless of whether it stayed within the package.
//! A `Target="../media/image1.png"` relationship -- the normal shape for an image that
//! lives at the package root's `media/` directory, one level above `word/` -- was silently
//! dropped: extraction still succeeded, but the image bytes were never attached (an
//! `ExtractedImage` entry is always pushed per drawing; only its `data` differs). The first
//! test below fails against the unfixed code, which returns empty `data` here instead of
//! the archived bytes. The second test pins that a target which truly escapes the package
//! (more `..` than there are directories to climb) still yields no image data.

#![cfg(feature = "office")]

mod helpers;
use helpers::extract_bytes_document_blocking;

use std::io::Write;
use xberg::{ExtractionConfig, ImageExtractionConfig};
use zip::write::{SimpleFileOptions, ZipWriter};

const WORD_MIME_TYPE: &str = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
const PNG_BYTES: &[u8] = &[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A];

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

/// Build a minimal, otherwise well-formed `.docx` whose single drawing's image
/// relationship `Target` is `image_target` verbatim, with a real PNG-signature entry
/// sitting at the package root's `media/image1.png`.
fn build_docx_with_image_target(image_target: &str) -> Vec<u8> {
    let mut buffer = Vec::new();
    {
        let mut zip = ZipWriter::new(std::io::Cursor::new(&mut buffer));
        let options = SimpleFileOptions::default();

        zip.start_file("[Content_Types].xml", options).unwrap();
        zip.write_all(
            br#"<?xml version="1.0" encoding="UTF-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
    <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
    <Default Extension="xml" ContentType="application/xml"/>
    <Default Extension="png" ContentType="image/png"/>
    <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>"#,
        )
        .unwrap();

        let document_xml = r#"<?xml version="1.0" encoding="UTF-8"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
            xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
            xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"
            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <w:body>
    <w:p>
      <w:r>
        <w:drawing>
          <wp:inline>
            <wp:extent cx="914400" cy="457200"/>
            <wp:docPr id="1" name="Picture 1"/>
            <a:graphic>
              <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <pic:pic>
                  <pic:blipFill>
                    <a:blip r:embed="rId5"/>
                  </pic:blipFill>
                </pic:pic>
              </a:graphicData>
            </a:graphic>
          </wp:inline>
        </w:drawing>
      </w:r>
    </w:p>
  </w:body>
</w:document>"#;
        zip.start_file("word/document.xml", options).unwrap();
        zip.write_all(document_xml.as_bytes()).unwrap();

        let rels_xml = format!(
            r#"<?xml version="1.0" encoding="UTF-8"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
    <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="{}"/>
</Relationships>"#,
            image_target
        );
        zip.start_file("word/_rels/document.xml.rels", options).unwrap();
        zip.write_all(rels_xml.as_bytes()).unwrap();

        // The image lives at the package root's media/ directory, one level above word/.
        zip.start_file("media/image1.png", options).unwrap();
        zip.write_all(PNG_BYTES).unwrap();

        let _ = zip.finish().unwrap();
    }
    buffer
}

/// The normal OPC shape for a DOCX image relationship whose target lives at the package
/// root's `media/` directory: `Target="../media/image1.png"`, resolved relative to
/// `word/_rels/document.xml.rels`'s own directory (`word/`). `word/../media` never crosses
/// the package root, so this is in-bounds and must resolve to real image bytes.
#[test]
fn test_parent_relative_target_resolves_to_package_root_media() {
    let data = build_docx_with_image_target("../media/image1.png");
    let config = image_extraction_config();

    let result = extract_bytes_document_blocking(&data, WORD_MIME_TYPE, &config)
        .expect("well-formed parent-relative image relationship must extract successfully");

    let images = result
        .images
        .as_ref()
        .expect("extraction with extract_images=true must populate the images field");
    assert_eq!(images.len(), 1, "expected exactly one drawing-derived image entry");
    assert_eq!(
        images[0].data.as_ref(),
        PNG_BYTES,
        "image bytes read via the resolved media/image1.png path must match what was archived"
    );
}

/// A relationship `Target` that pops past the package root (more `..` than there are
/// directories to climb) must not yield image data. Extraction still succeeds -- the
/// drawing still produces an `ExtractedImage` entry -- but its `data` stays empty rather
/// than reaching for a path outside the package, matching the "malformed input degrades
/// gracefully" rule.
#[test]
fn test_target_escaping_the_package_root_yields_no_image_data() {
    let data = build_docx_with_image_target("../../../../../etc/passwd");
    let config = image_extraction_config();

    let result = extract_bytes_document_blocking(&data, WORD_MIME_TYPE, &config)
        .expect("malformed relationship target must not fail extraction outright");

    let images = result
        .images
        .as_ref()
        .expect("extraction with extract_images=true must populate the images field");
    assert_eq!(images.len(), 1, "expected exactly one drawing-derived image entry");
    assert!(
        images[0].data.is_empty(),
        "an out-of-bounds relationship target must not yield image data"
    );
}
