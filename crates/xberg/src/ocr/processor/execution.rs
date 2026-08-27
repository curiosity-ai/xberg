//! OCR execution and result processing.
//!
//! This module handles the core OCR execution logic, including image processing,
//! text extraction, and result formatting.

use super::api_pool::TesseractApiPool;
use super::config::{apply_tesseract_variables, hash_config};
use super::validation::{
    resolve_all_installed_languages, resolve_tessdata_path, strip_control_characters, validate_language_and_traineddata,
};
use crate::core::config::ExtractionConfig;
use crate::image::normalize_image_dpi_owned;
use crate::ocr::cache::OcrCache;
use crate::ocr::conversion::{TsvRow, iterator_word_to_element, tsv_row_to_element};
use crate::ocr::error::OcrError;
use crate::ocr::hocr_parser::{DictionaryLineFilter, parse_hocr_to_internal_document_with_page_offset};
#[cfg(feature = "pdf")]
use crate::ocr::table::post_process_table;
use crate::ocr::table::{extract_words_from_tsv, reconstruct_table, table_to_markdown};
#[cfg(test)]
use crate::ocr::types::BatchItemResult;
use crate::ocr::types::TesseractConfig;
use crate::types::internal::{ElementKind, InternalDocument};
use crate::types::{OcrExtractionResult, OcrTable, OcrTableBoundingBox};
use std::collections::HashMap;
use std::env;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use xberg_tesseract::{TessPageSegMode, TessPolyBlockType, TesseractAPI};

/// Process-global document-orientation classifier (ONNX PP-LCNet), shared by
/// every tesseract-backend auto-rotate call. Session initialization is lazy and
/// internally synchronized; `detect` is thread-safe.
#[cfg(auto_rotate)]
fn doc_orientation_detector() -> &'static crate::doc_orientation::DocOrientationDetector {
    static DETECTOR: std::sync::LazyLock<crate::doc_orientation::DocOrientationDetector> =
        std::sync::LazyLock::new(|| {
            crate::doc_orientation::DocOrientationDetector::with_acceleration(
                crate::doc_orientation::resolve_cache_dir(),
                None,
            )
        });
    &DETECTOR
}

use crate::table_core::{MIN_TABLE_CANDIDATE_WORDS, cluster_words_into_table_regions};
use crate::types::OcrElement;

#[cfg(auto_rotate)]
/// Rotate raw RGB image data by the given degrees (0, 90, 180, 270).
///
/// Returns the rotated pixel data and the new (width, height).
/// For 90° and 270° rotations, width and height are swapped.
fn rotate_rgb_image_data(data: &[u8], width: u32, height: u32, degrees: i32) -> (Vec<u8>, u32, u32) {
    let bpp = 3usize;
    let w = width as usize;
    let h = height as usize;

    match degrees {
        0 => (data.to_vec(), width, height),
        90 => {
            let new_w = h;
            let new_h = w;
            let mut out = vec![0u8; new_w * new_h * bpp];
            for y in 0..h {
                for x in 0..w {
                    let src_idx = (y * w + x) * bpp;
                    let dst_x = h - 1 - y;
                    let dst_y = x;
                    let dst_idx = (dst_y * new_w + dst_x) * bpp;
                    out[dst_idx..dst_idx + bpp].copy_from_slice(&data[src_idx..src_idx + bpp]);
                }
            }
            (out, new_w as u32, new_h as u32)
        }
        180 => {
            let mut out = vec![0u8; w * h * bpp];
            for y in 0..h {
                for x in 0..w {
                    let src_idx = (y * w + x) * bpp;
                    let dst_x = w - 1 - x;
                    let dst_y = h - 1 - y;
                    let dst_idx = (dst_y * w + dst_x) * bpp;
                    out[dst_idx..dst_idx + bpp].copy_from_slice(&data[src_idx..src_idx + bpp]);
                }
            }
            (out, width, height)
        }
        270 => {
            let new_w = h;
            let new_h = w;
            let mut out = vec![0u8; new_w * new_h * bpp];
            for y in 0..h {
                for x in 0..w {
                    let src_idx = (y * w + x) * bpp;
                    let dst_x = y;
                    let dst_y = w - 1 - x;
                    let dst_idx = (dst_y * new_w + dst_x) * bpp;
                    out[dst_idx..dst_idx + bpp].copy_from_slice(&data[src_idx..src_idx + bpp]);
                }
            }
            (out, new_w as u32, new_h as u32)
        }
        _ => {
            tracing::warn!("Unsupported rotation angle: {}°, skipping rotation", degrees);
            (data.to_vec(), width, height)
        }
    }
}

/// Parse Tesseract TSV output into structured OcrElements.
///
/// TSV format columns: level, page_num, block_num, par_num, line_num, word_num, left, top, width, height, conf, text
///
/// # Arguments
///
/// * `tsv_data` - Raw TSV output from Tesseract
/// * `min_confidence` - Minimum confidence threshold (0-100 scale)
/// * `page_number` - The true, 1-indexed page number of the source document this TSV came
///   from. Tesseract's own `page_num` TSV column is always `1` for a single-image call (see
///   `TesseractConfig::page_number`'s doc comment), so it is used only to build each
///   element's opaque `parent_id`, never as the returned elements' page number.
///
/// # Returns
///
/// Vector of OcrElements for word-level and line-level entries
fn parse_tsv_to_elements(tsv_data: &str, min_confidence: f64, page_number: u32) -> Vec<OcrElement> {
    let mut elements = Vec::new();

    for line in tsv_data.lines().skip(1) {
        let fields: Vec<&str> = line.split('\t').collect();
        if fields.len() < 12 {
            continue;
        }

        let level = fields[0].parse::<i32>().unwrap_or(0);
        let page_num = fields[1].parse::<i32>().unwrap_or(1);
        let block_num = fields[2].parse::<i32>().unwrap_or(0);
        let par_num = fields[3].parse::<i32>().unwrap_or(0);
        let line_num = fields[4].parse::<i32>().unwrap_or(0);
        let word_num = fields[5].parse::<i32>().unwrap_or(0);
        let left = fields[6].parse::<u32>().unwrap_or(0);
        let top = fields[7].parse::<u32>().unwrap_or(0);
        let width = fields[8].parse::<u32>().unwrap_or(0);
        let height = fields[9].parse::<u32>().unwrap_or(0);
        let conf = fields[10].parse::<f64>().unwrap_or(-1.0);
        let text = fields[11].to_string();

        if conf < 0.0 || conf < min_confidence || text.trim().is_empty() {
            continue;
        }

        if level != 4 && level != 5 {
            continue;
        }

        let tsv_row = TsvRow {
            level,
            page_num,
            block_num,
            par_num,
            line_num,
            word_num,
            left,
            top,
            width,
            height,
            conf,
            text,
        };

        let mut element = tsv_row_to_element(&tsv_row);
        element.page_number = page_number;
        elements.push(element);
    }

    elements
}

/// CI debug logging utility.
///
/// Logs debug messages when XBERG_CI_DEBUG environment variable is set.
fn log_ci_debug<F>(enabled: bool, stage: &str, details: F)
where
    F: FnOnce() -> String,
{
    if !enabled {
        return;
    }

    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs_f64())
        .unwrap_or(0.0);

    tracing::debug!(stage, timestamp = format!("{timestamp:.3}"), "{}", details());
}

/// Build content with OCR tables inlined at their correct vertical positions.
///
/// Parses TSV word positions to separate table words from non-table words,
/// groups non-table words into lines and paragraphs, then interleaves
/// paragraphs and table markdown sorted by Y-position.
fn build_content_with_inline_tables(tsv_data: &str, tables: &[OcrTable], min_confidence: f64) -> String {
    let words = match extract_words_from_tsv(tsv_data, min_confidence) {
        Ok(w) => w,
        Err(_) => return String::new(),
    };

    if words.is_empty() {
        return String::new();
    }

    let table_bboxes: Vec<_> = tables.iter().filter_map(|t| t.bounding_box.as_ref()).collect();

    let mut non_table_words = Vec::new();
    for word in &words {
        let in_table = table_bboxes.iter().any(|bbox| {
            let word_cx = word.left + word.width / 2;
            let word_cy = word.top + word.height / 2;
            word_cx >= bbox.left && word_cx <= bbox.right && word_cy >= bbox.top && word_cy <= bbox.bottom
        });
        if !in_table {
            non_table_words.push(word);
        }
    }

    if non_table_words.is_empty() && tables.is_empty() {
        return String::new();
    }

    let mut sorted_words = non_table_words;
    sorted_words.sort_by(|a, b| a.top.cmp(&b.top).then(a.left.cmp(&b.left)));

    let avg_height = if sorted_words.is_empty() {
        20
    } else {
        let total_h: u32 = sorted_words.iter().map(|w| w.height).sum();
        (total_h / sorted_words.len() as u32).max(1)
    };
    let line_threshold = avg_height / 2;

    struct TextLine {
        y_center: u32,
        text: String,
    }

    let mut lines: Vec<TextLine> = Vec::new();
    for word in &sorted_words {
        let word_y = word.top + word.height / 2;
        if let Some(last_line) = lines.last_mut()
            && word_y.abs_diff(last_line.y_center) <= line_threshold
        {
            last_line.text.push(' ');
            last_line.text.push_str(&word.text);
            continue;
        }
        lines.push(TextLine {
            y_center: word_y,
            text: word.text.clone(),
        });
    }

    let paragraph_gap = avg_height * 2;

    struct Paragraph {
        y_start: u32,
        text: String,
    }

    let mut paragraphs: Vec<Paragraph> = Vec::new();
    for line in &lines {
        if let Some(last_para) = paragraphs.last_mut() {
            let last_y = last_para.y_start;
            if line.y_center.saturating_sub(last_y) <= paragraph_gap {
                last_para.text.push('\n');
                last_para.text.push_str(&line.text);
                last_para.y_start = line.y_center;
                continue;
            }
        }
        paragraphs.push(Paragraph {
            y_start: line.y_center,
            text: line.text.clone(),
        });
    }

    enum ContentElement<'a> {
        Paragraph { y: u32, text: String },
        Table { y: u32, markdown: &'a str },
    }

    let mut elements: Vec<ContentElement> = Vec::new();

    {
        let mut para_idx = 0;
        let mut line_idx = 0;
        for para in &paragraphs {
            let line_count = para.text.matches('\n').count() + 1;
            let first_y = if line_idx < lines.len() {
                lines[line_idx].y_center
            } else {
                para.y_start
            };
            elements.push(ContentElement::Paragraph {
                y: first_y,
                text: para.text.clone(),
            });
            line_idx += line_count;
            para_idx += 1;
        }
        let _ = para_idx;
    }

    for table in tables {
        if let Some(ref bbox) = table.bounding_box {
            elements.push(ContentElement::Table {
                y: bbox.top,
                markdown: &table.markdown,
            });
        } else {
            elements.push(ContentElement::Table {
                y: u32::MAX,
                markdown: &table.markdown,
            });
        }
    }

    elements.sort_by_key(|e| match e {
        ContentElement::Paragraph { y, .. } => *y,
        ContentElement::Table { y, .. } => *y,
    });

    let mut output = String::new();
    for elem in &elements {
        let text = match elem {
            ContentElement::Paragraph { text, .. } => text.as_str(),
            ContentElement::Table { markdown, .. } => markdown,
        };
        let trimmed = text.trim();
        if trimmed.is_empty() {
            continue;
        }
        if !output.is_empty() {
            output.push_str("\n\n");
        }
        output.push_str(trimmed);
    }

    output
}

/// Flatten hOCR-derived paragraph elements to unformatted text, joined by
/// blank lines (#185).
///
/// This used to also promote large-font, short, single-line paragraphs to
/// `## ` markdown headings using the average `x_fsize` captured per
/// paragraph by `hocr_parser::parse_hocr_to_internal_document`. That baked
/// markdown syntax directly into `OcrExtractionResult::content`, which is
/// consumed unrendered by callers that expect `Plain` output (e.g. the
/// `flat_ocr_page_document` fallback in `extractors/pdf/ocr.rs`), leaking
/// `## ` into supposedly-plain text. It also duplicated the document-global
/// structure pipeline, which classifies headings from the same `x_fsize`
/// attribute across the whole document and is strictly better informed than
/// a single page's local heuristic. That pipeline (driven by
/// `hocr_document`/`internal_document`, not this string) is now the sole
/// owner of heading detection for routes that have it; this function only
/// flattens text. The standalone-image path, which has no structure
/// pipeline behind it, keeps its own font-size-based heading promotion in
/// `extractors/image.rs`, expressed as real `Heading` elements rather than
/// pre-escaped markdown text.
fn flatten_hocr_elements_to_text(elements: &[crate::types::internal::InternalElement]) -> String {
    elements
        .iter()
        .filter(|e| !matches!(e.kind, ElementKind::PageBreak) && !e.text.is_empty())
        .map(|e| e.text.as_str())
        .collect::<Vec<_>>()
        .join("\n\n")
}

/// Minimum confidence for accepting orientation detection results.
///
/// Keep in sync with `doc_orientation::MIN_CONFIDENCE` (module is feature-gated,
/// this const is not): the PP-LCNet classifier reports correct 90°/270° rotations
/// on real documents at ~0.45, so a 0.5 cutoff rejects valid detections while
/// 0° false positives above 0.35 are rare.
const MIN_ORIENTATION_CONFIDENCE: f32 = 0.35;

#[cfg(auto_rotate)]
const _: () = assert!(MIN_ORIENTATION_CONFIDENCE == crate::doc_orientation::MIN_CONFIDENCE);

/// Grayscale mean (0–255) below which the image is considered background-
/// dark rather than background-light.
///
/// Scanned documents are background-dominated (background typically covers
/// 85–95% of pixel area), so the grayscale mean tracks background tone far
/// more than foreground text tone. A mean below this value means most of the
/// image is dark, i.e. a plausible dark background.
const DARK_BACKGROUND_MEAN_THRESHOLD: f64 = 100.0;

/// Grayscale value (0–255) at or above which a pixel counts as "light" when
/// computing the light-pixel fraction used to guard polarity auto-detection.
const LIGHT_PIXEL_VALUE_THRESHOLD: u8 = 180;

/// Minimum fraction of light pixels required, alongside a dark mean, before
/// auto-detection treats an image as light-text-on-dark-background.
///
/// Without this guard, a naive `mean < threshold` check would also fire on
/// uniformly dark, non-text images (e.g. a dark photograph or a heavily
/// shadowed scan) where inverting would corrupt the image rather than fix
/// it. Real text/foreground content typically covers at least a small
/// fraction of a page, so requiring *some* light pixels alongside the dark
/// mean distinguishes "light text on a dark background" from "uniformly
/// dark image".
const MIN_LIGHT_PIXEL_FRACTION_FOR_INVERT: f64 = 0.01;

/// Sampling grid spacing (pixels) used when computing polarity statistics.
/// Keeps polarity detection cheap on large scans while remaining
/// representative.
const POLARITY_SAMPLE_STRIDE: i32 = 4;

/// Source resolution assumed when OCR receives the original image unchanged and the caller did
/// not tell us what resolution it really is.
///
/// Only a fallback. `TesseractConfig::source_dpi`, when the caller knows the true value, takes
/// precedence — see [`prepare_ocr_image`].
const RAW_IMAGE_SOURCE_DPI: i32 = 72;
/// Source resolution retained when explicit DPI normalization fails.
const PREPROCESSING_FALLBACK_SOURCE_DPI: i32 = 300;
/// Pixel stride used to classify whether an image resembles a clean document page.
const DEFAULT_PREPROCESSING_SAMPLE_STRIDE: usize = 4;
/// Minimum normalized mean luminance for automatic document-page preprocessing.
const CLEAN_PAGE_MEAN_LUMINANCE_THRESHOLD: f64 = 0.90;
/// Normalized luminance at which a sampled pixel counts as near-white.
const CLEAN_PAGE_LIGHT_PIXEL_THRESHOLD: f64 = 0.90;
/// Minimum near-white sample fraction for automatic document-page preprocessing.
const CLEAN_PAGE_LIGHT_PIXEL_FRACTION_THRESHOLD: f64 = 0.80;
/// Maximum value of an 8-bit RGB channel.
const RGB_CHANNEL_MAX: f64 = u8::MAX as f64;
/// Number of channels in an RGB pixel.
const RGB_CHANNEL_COUNT: usize = 3;

struct PreparedOcrImage {
    data: Vec<u8>,
    width: u32,
    height: u32,
    source_dpi: i32,
    apply_pix_preprocessing: bool,
    image_preprocessing: Option<crate::types::ImagePreprocessingMetadata>,
}

/// Prepare the raster Tesseract will recognize, and report the resolution it should be told.
///
/// `known_source_dpi` is the true resolution of `rgb_data` when the caller knows it (the PDF OCR
/// route derives it from the render), and `None` when it genuinely does not (raw images handed in
/// by a user). Both branches below honour it: the unpreprocessed branch reports it verbatim
/// instead of the [`RAW_IMAGE_SOURCE_DPI`] assumption, and the preprocessed branch feeds it to
/// DPI normalization so the resize scales from the real resolution.
fn prepare_ocr_image(
    rgb_data: Vec<u8>,
    width: u32,
    height: u32,
    preprocessing: Option<&crate::types::ImagePreprocessingConfig>,
    images_config: Option<&crate::core::config::ImageExtractionConfig>,
    ci_debug_enabled: bool,
    known_source_dpi: Option<f64>,
) -> PreparedOcrImage {
    let Some(preprocessing) = preprocessing else {
        if should_apply_default_preprocessing(&rgb_data) {
            return prepare_preprocessed_ocr_image(
                rgb_data,
                width,
                height,
                &crate::types::ImagePreprocessingConfig::default(),
                images_config,
                ci_debug_enabled,
                known_source_dpi,
            );
        }
        return PreparedOcrImage {
            data: rgb_data,
            width,
            height,
            // The image is passed through untouched, so its resolution is whatever the caller
            // says it is; 72 remains the assumption only when nobody knows.
            source_dpi: known_source_dpi.map_or(RAW_IMAGE_SOURCE_DPI, |dpi| dpi.round() as i32),
            apply_pix_preprocessing: false,
            image_preprocessing: None,
        };
    };

    prepare_preprocessed_ocr_image(
        rgb_data,
        width,
        height,
        preprocessing,
        images_config,
        ci_debug_enabled,
        known_source_dpi,
    )
}

/// Classify bright, page-like RGB images that benefit from the default OCR preprocessing path.
fn should_apply_default_preprocessing(rgb_data: &[u8]) -> bool {
    let mut luminance_sum = 0.0;
    let mut light_pixels = 0usize;
    let mut sample_count = 0usize;

    for pixel in rgb_data
        .chunks_exact(RGB_CHANNEL_COUNT)
        .step_by(DEFAULT_PREPROCESSING_SAMPLE_STRIDE)
    {
        let luminance =
            pixel.iter().map(|channel| f64::from(*channel)).sum::<f64>() / (RGB_CHANNEL_MAX * RGB_CHANNEL_COUNT as f64);
        luminance_sum += luminance;
        light_pixels += usize::from(luminance >= CLEAN_PAGE_LIGHT_PIXEL_THRESHOLD);
        sample_count += 1;
    }

    if sample_count == 0 {
        return false;
    }
    let sample_count = sample_count as f64;
    luminance_sum / sample_count >= CLEAN_PAGE_MEAN_LUMINANCE_THRESHOLD
        && light_pixels as f64 / sample_count >= CLEAN_PAGE_LIGHT_PIXEL_FRACTION_THRESHOLD
}

fn prepare_preprocessed_ocr_image(
    rgb_data: Vec<u8>,
    width: u32,
    height: u32,
    preprocessing: &crate::types::ImagePreprocessingConfig,
    images_config: Option<&crate::core::config::ImageExtractionConfig>,
    ci_debug_enabled: bool,
    known_source_dpi: Option<f64>,
) -> PreparedOcrImage {
    // `target_dpi` always comes from the (Tesseract-specific) `preprocessing` config, which
    // takes precedence when explicitly set. The dimension/auto-adjust limits have no home in
    // `ImagePreprocessingConfig`, so they come from the real `ImageExtractionConfig` when the
    // caller has one, instead of being silently defaulted (issue #209).
    let dpi_config = match images_config {
        Some(images_config) => crate::types::ImageDpiConfig {
            target_dpi: preprocessing.target_dpi,
            ..crate::types::ImageDpiConfig::from(images_config)
        },
        None => crate::types::ImageDpiConfig {
            target_dpi: preprocessing.target_dpi,
            ..Default::default()
        },
    };
    // `known_source_dpi` is the whole point of this parameter: passing `None` here makes
    // `normalize_image_dpi_owned` assume 72 DPI, which on a page rendered at 150 inflates the
    // scale factor to `target_dpi / 72` and drives a Letter page into the `max_image_dimension`
    // clamp — 6x the pixels of the correct resize, carrying no more information, and reported to
    // Tesseract as roughly half the raster's real `scan_res`.
    match normalize_image_dpi_owned(rgb_data, width as usize, height as usize, &dpi_config, known_source_dpi) {
        Ok(result) => {
            let normalized_width = result.dimensions.0 as u32;
            let normalized_height = result.dimensions.1 as u32;
            let source_dpi = result.metadata.final_dpi;
            log_ci_debug(ci_debug_enabled, "dpi_normalization", || {
                format!(
                    "original={}x{} normalized={}x{} target_dpi={} final_dpi={} resized={}",
                    width,
                    height,
                    normalized_width,
                    normalized_height,
                    result.metadata.target_dpi,
                    source_dpi,
                    !result.metadata.skipped_resize
                )
            });
            PreparedOcrImage {
                data: result.rgb_data,
                width: normalized_width,
                height: normalized_height,
                source_dpi,
                apply_pix_preprocessing: true,
                image_preprocessing: Some(result.metadata),
            }
        }
        Err((error, data)) => {
            tracing::warn!("DPI normalization failed, using original image: {}", error);
            PreparedOcrImage {
                data,
                width,
                height,
                // The image comes back unresized here, so its resolution is still the source
                // one. The 300 fallback is a guess for when there is nothing better.
                source_dpi: known_source_dpi.map_or(PREPROCESSING_FALLBACK_SOURCE_DPI, |dpi| dpi.round() as i32),
                apply_pix_preprocessing: true,
                image_preprocessing: None,
            }
        }
    }
}

/// Decide whether an image should be inverted before OCR.
///
/// `force_invert` mirrors `ImagePreprocessingConfig.invert_colors`: `true`
/// unconditionally forces inversion (an explicit override for cases where
/// auto-detection disagrees); `false` (the default) falls back to
/// auto-detection from `mean_gray` and `light_fraction` — see
/// `DARK_BACKGROUND_MEAN_THRESHOLD` and `MIN_LIGHT_PIXEL_FRACTION_FOR_INVERT`.
///
/// This is a pure function so the polarity decision can be unit-tested
/// without linking native Tesseract/Leptonica.
fn should_invert_for_polarity(mean_gray: f64, light_fraction: f64, force_invert: bool) -> bool {
    force_invert
        || (mean_gray < DARK_BACKGROUND_MEAN_THRESHOLD && light_fraction >= MIN_LIGHT_PIXEL_FRACTION_FOR_INVERT)
}

/// Preprocess a raw Pix before handing it to Tesseract.
///
/// Order matters: polarity (light-on-dark vs. dark-on-light) is detected and
/// corrected *first*, because `background_normalize` assumes a light
/// background and Tesseract's own binarizer assumes dark text on a light
/// background. `force_invert` mirrors `ImagePreprocessingConfig.invert_colors`
/// (see [`should_invert_for_polarity`]).
fn preprocess_pix(pix: xberg_tesseract::Pix, force_invert: bool) -> xberg_tesseract::Result<xberg_tesseract::Pix> {
    let polarity_stats = pix
        .to_grayscale()
        .and_then(|gray| gray.grayscale_stats(LIGHT_PIXEL_VALUE_THRESHOLD, POLARITY_SAMPLE_STRIDE))
        .ok();

    let invert = match polarity_stats {
        Some((mean_gray, light_fraction)) => should_invert_for_polarity(mean_gray, light_fraction, force_invert),
        None => force_invert,
    };

    let source = if invert {
        let inverted = pix.invert()?;
        drop(pix);
        inverted
    } else {
        pix
    };

    let normalized = source.background_normalize()?;
    drop(source);

    let sharpened = normalized.unsharp_mask(3, 0.5)?;
    drop(normalized);

    let grayscale = sharpened.to_grayscale()?;
    drop(sharpened);
    Ok(grayscale)
}

/// Check whether a center point (x, y) lies within a bounding box.
fn point_in_bbox(x: i32, y: i32, left: i32, top: i32, right: i32, bottom: i32) -> bool {
    x >= left && x <= right && y >= top && y <= bottom
}

/// Result of [`extract_elements_via_iterator`]: the extracted elements plus
/// counters distinguishing "nothing to extract" from "extraction degraded"
/// (#192, #180).
struct IteratorExtractionResult {
    elements: Vec<OcrElement>,
    /// Words the Tesseract result iterator itself failed to extract
    /// (null pointer / invalid parameter / invalid UTF-8 per word), per
    /// `ResultIterator::extract_all_words` (#192).
    skipped_words: usize,
    /// Words whose parent block is a non-flowing-text type (noise, an
    /// embedded image region, or a ruling line) that are now retained in
    /// `elements` instead of being silently dropped (#180).
    non_text_block_word_count: usize,
    /// Fraction of this page's dictionary-checkable words that Tesseract's own DAWG
    /// dictionary (`TesseractAPI::is_valid_word`) rejects as not-a-word. See
    /// [`dictionary_invalid_word_ratio`] for what counts as checkable and why `None`
    /// (rather than `0.0`) means "too few checkable words to be meaningful".
    dict_invalid_word_ratio: Option<f64>,
}

/// Minimum letters a word must have before Tesseract's dictionary lookup is meaningful.
/// Digits, single letters, and punctuation are never in the DAWG dictionary regardless of
/// legitimacy, so scoring them would bias the ratio toward "invalid" on output OCR read
/// perfectly correctly.
const MIN_WORD_LEN_FOR_DICT_CHECK: usize = 3;

/// Minimum number of dictionary-checkable words on a page before
/// [`dictionary_invalid_word_ratio`] reports a ratio at all, mirroring the same
/// conservatism as `OcrQualityThresholds::min_words_for_ocr_output_check`: a short page
/// (a signature block, an exhibit title) does not carry enough checkable words for the
/// ratio to mean anything.
const MIN_DICT_CANDIDATES_FOR_RATIO: usize = 5;

/// Fraction of a page's alphabetic, dictionary-checkable words that Tesseract's own
/// dictionary rejects as not-a-word.
///
/// This is the dictionary-validity supplement to the recognition-noise gate
/// (`is_ocr_recognition_noise` in `extractors::pdf::ocr`): a page of scanned line art
/// noise ("LAAALDLI", "AEA") is read by Tesseract with confident-looking output, so
/// per-word OCR confidence and the fragmented-word-ratio heuristic do not always catch
/// it — but few of its "words" are real dictionary words. `TesseractAPI::is_valid_word`
/// is Tesseract's own DAWG dictionary lookup, and is only usable while the `TesseractAPI`
/// handle that performed this page's OCR is still open (the dictionary lives inside that
/// engine instance) — callers must call this before the API is dropped.
///
/// Returns `None`, never `0.0`, when there are too few dictionary-checkable words to make
/// a ratio meaningful: a `0.0` here would read as "every word is valid" to a caller that
/// cannot tell "no evidence" from "good evidence", and this signal is a veto input, so
/// that conflation would suppress real content.
fn dictionary_invalid_word_ratio(api: &TesseractAPI, words: &[xberg_tesseract::WordData]) -> Option<f64> {
    invalid_word_ratio_from(words, |text| match api.is_valid_word(text) {
        Ok(0) => Some(false),
        Ok(_) => Some(true),
        // Lookup failed (e.g. interior NUL byte); don't let it bias the ratio either way.
        Err(_) => None,
    })
}

/// Pure core of [`dictionary_invalid_word_ratio`], taking dictionary lookup as an injected
/// function (`Some(true)` valid, `Some(false)` invalid, `None` lookup failed / skip) so it
/// is unit-testable without a live `TesseractAPI` handle.
fn invalid_word_ratio_from(
    words: &[xberg_tesseract::WordData],
    is_valid: impl Fn(&str) -> Option<bool>,
) -> Option<f64> {
    let mut candidates = 0usize;
    let mut invalid = 0usize;
    for word in words {
        let text = word.text.trim();
        if text.chars().count() < MIN_WORD_LEN_FOR_DICT_CHECK || !text.chars().all(|c| c.is_alphabetic()) {
            continue;
        }
        match is_valid(text) {
            Some(true) => candidates += 1,
            Some(false) => {
                candidates += 1;
                invalid += 1;
            }
            None => {}
        }
    }
    if candidates < MIN_DICT_CANDIDATES_FOR_RATIO {
        return None;
    }
    Some(invalid as f64 / candidates as f64)
}

/// Block types that historically caused a word to be dropped entirely from
/// `ocr_elements`. Words in these blocks are now retained (tagged with their
/// `block_type` via `iterator_word_to_element`) so text baked into figures,
/// chart axis labels, pull-out captions, and ruling-line regions is not lost
/// from OCR output (#180). Kept as a named set purely to identify and count
/// them for the `non_text_block_word_count` signal.
const NON_TEXT_BLOCK_TYPES: [TessPolyBlockType; 6] = [
    TessPolyBlockType::PT_NOISE,
    TessPolyBlockType::PT_FLOWING_IMAGE,
    TessPolyBlockType::PT_HEADING_IMAGE,
    TessPolyBlockType::PT_PULLOUT_IMAGE,
    TessPolyBlockType::PT_HORZ_LINE,
    TessPolyBlockType::PT_VERT_LINE,
];

/// Whether `auto_rotate` was requested by the caller but orientation detection
/// could not run because the `auto-rotate` build feature is not compiled in.
///
/// Extracted as a pure function, separate from the surrounding OCR pipeline and
/// its FFI calls, so the request-vs-availability logic is unit-testable without
/// a live Tesseract API instance (#309).
fn is_auto_rotate_requested_but_unavailable(auto_rotate_enabled: bool) -> bool {
    #[cfg(auto_rotate)]
    {
        let _ = auto_rotate_enabled;
        false
    }
    #[cfg(not(auto_rotate))]
    {
        auto_rotate_enabled
    }
}

/// Inserts the `word_iterator_skipped_count` metadata key only when at least one
/// word was actually skipped by the Tesseract result iterator, so callers can use
/// the key's absence (rather than a `0` value) as the "clean extraction" signal.
///
/// Extracted as its own function, separate from the surrounding OCR pipeline, so
/// the presence/absence and exact-value behaviour is unit-testable without a live
/// Tesseract API instance (#192).
fn insert_word_iterator_skipped_count_metadata(
    metadata: &mut HashMap<String, serde_json::Value>,
    skipped_words: usize,
) {
    if skipped_words > 0 {
        metadata.insert(
            "word_iterator_skipped_count".to_string(),
            serde_json::Value::Number(skipped_words.into()),
        );
    }
}

/// Extract OcrElements via Tesseract's iterator APIs with rich metadata.
///
/// Uses ResultIterator for word-level text, bounding boxes, confidence, and font
/// attributes, plus PageIterator for block type and paragraph info. This replaces
/// TSV-based extraction with significantly richer metadata.
fn extract_elements_via_iterator(
    api: &TesseractAPI,
    page_number: u32,
    min_confidence: f64,
) -> Result<IteratorExtractionResult, OcrError> {
    let empty = || IteratorExtractionResult {
        elements: Vec::new(),
        skipped_words: 0,
        non_text_block_word_count: 0,
        dict_invalid_word_ratio: None,
    };

    let page_iter = match api.get_page_iterator() {
        Ok(iter) => iter,
        Err(e) => {
            tracing::warn!(error = %e, "Tesseract page iterator unavailable; falling back to TSV-based OCR element extraction");
            return Ok(empty());
        }
    };

    let blocks = match page_iter.extract_all_blocks() {
        Ok(b) => b,
        Err(e) => {
            tracing::warn!(error = %e, "Failed to extract Tesseract page blocks; falling back to TSV-based OCR element extraction");
            return Ok(empty());
        }
    };

    let paragraph_extraction = match page_iter.extract_all_paragraphs() {
        Ok(p) => p,
        Err(e) => {
            tracing::warn!(error = %e, "Failed to extract Tesseract page paragraphs; falling back to TSV-based OCR element extraction");
            return Ok(empty());
        }
    };

    if paragraph_extraction.skipped_no_para_info > 0 || paragraph_extraction.skipped_no_bbox > 0 {
        tracing::warn!(
            skipped_no_para_info = paragraph_extraction.skipped_no_para_info,
            skipped_no_bbox = paragraph_extraction.skipped_no_bbox,
            extracted_paragraphs = paragraph_extraction.paragraphs.len(),
            "Tesseract declined to describe some paragraphs; their words lose the is_crown, \
             is_list_item and justification metadata"
        );
    }

    let paragraphs = paragraph_extraction.paragraphs;

    let result_iter = match api.get_iterator() {
        Ok(iter) => iter,
        Err(e) => {
            tracing::warn!(error = %e, "Tesseract result iterator unavailable; falling back to TSV-based OCR element extraction");
            return Ok(empty());
        }
    };

    let word_extraction = match result_iter.extract_all_words() {
        Ok(w) => w,
        Err(e) => {
            tracing::warn!(error = %e, "Tesseract result iterator failed; falling back to TSV-based OCR element extraction");
            return Ok(empty());
        }
    };

    if word_extraction.skipped > 0 {
        tracing::warn!(
            skipped_words = word_extraction.skipped,
            extracted_words = word_extraction.words.len(),
            "Tesseract result iterator failed to extract some words (null pointer, invalid \
             parameter, or invalid UTF-8); they were dropped from OCR output"
        );
    }

    let dict_invalid_word_ratio = dictionary_invalid_word_ratio(api, &word_extraction.words);

    let mut elements = Vec::new();
    let mut non_text_block_word_count = 0usize;

    for word in &word_extraction.words {
        if (word.confidence as f64) < min_confidence {
            continue;
        }

        if word.text.trim().is_empty() {
            continue;
        }

        let cx = (word.left + word.right) / 2;
        let cy = (word.top + word.bottom) / 2;

        let parent_block = blocks
            .iter()
            .find(|b| point_in_bbox(cx, cy, b.left, b.top, b.right, b.bottom));

        let block_type = parent_block.map(|b| b.block_type);

        if let Some(bt) = block_type
            && NON_TEXT_BLOCK_TYPES.contains(&bt)
        {
            non_text_block_word_count += 1;
        }

        let para_info = paragraphs
            .iter()
            .find(|p| point_in_bbox(cx, cy, p.left, p.top, p.right, p.bottom));

        let element = iterator_word_to_element(word, block_type, para_info, page_number);
        elements.push(element);
    }

    Ok(IteratorExtractionResult {
        elements,
        skipped_words: word_extraction.skipped,
        non_text_block_word_count,
        dict_invalid_word_ratio,
    })
}

/// Perform OCR on an image using Tesseract.
///
/// This function handles the complete OCR pipeline:
/// 1. Image loading and preprocessing
/// 2. Tesseract initialization and configuration
/// 3. Text recognition
/// 4. Output formatting (text, markdown, hOCR, or TSV)
/// 5. Optional table detection
///
/// # Arguments
///
/// * `image_bytes` - Raw image data
/// * `config` - OCR configuration
/// * `extraction_config` - Optional extraction config for output format (markdown vs djot)
///
/// # Returns
///
/// OCR extraction result containing text and optional tables
pub(super) fn perform_ocr(
    image_bytes: &[u8],
    config: &TesseractConfig,
    api_pool: &Arc<TesseractApiPool>,
    extraction_config: Option<&ExtractionConfig>,
) -> Result<OcrExtractionResult, OcrError> {
    let ci_debug_enabled = env::var_os("XBERG_CI_DEBUG").is_some();
    log_ci_debug(ci_debug_enabled, "perform_ocr:start", || {
        format!(
            "bytes={} language={} output={} use_cache={}",
            image_bytes.len(),
            config.language,
            config.output_format,
            config.use_cache
        )
    });

    let rgb_image = {
        let img = crate::extraction::image::load_image_for_ocr(image_bytes)
            .map_err(|e| OcrError::ImageProcessingFailed(e.to_string()))?;
        img.into_rgb8()
    };
    let (orig_width, orig_height) = rgb_image.dimensions();
    let rgb_data = rgb_image.into_raw();

    log_ci_debug(ci_debug_enabled, "image", || {
        format!("dimensions={}x{} color_type=RGB8", orig_width, orig_height)
    });

    let images_config = extraction_config.and_then(|extraction_config| extraction_config.images.as_ref());
    let prepared_image = prepare_ocr_image(
        rgb_data,
        orig_width,
        orig_height,
        config.preprocessing.as_ref(),
        images_config,
        ci_debug_enabled,
        config.source_dpi,
    );
    let image_data = prepared_image.data;
    let width = prepared_image.width;
    let height = prepared_image.height;
    let source_dpi = prepared_image.source_dpi;
    let image_preprocessing = prepared_image.image_preprocessing;
    #[cfg_attr(not(auto_rotate), allow(unused_mut))]
    let mut ocr_image_width = width;
    #[cfg_attr(not(auto_rotate), allow(unused_mut))]
    let mut ocr_image_height = height;

    let bytes_per_pixel: u32 = 3;
    let bytes_per_line = width * bytes_per_pixel;

    let languages: Vec<String> = config.language.split('+').map(|lang| lang.trim().to_string()).collect();
    let tessdata_path = resolve_tessdata_path(&languages, config.tessdata_path.as_deref())?;

    log_ci_debug(ci_debug_enabled, "tessdata", || {
        let path_preview = env::var_os("PATH").map(|paths| {
            env::split_paths(&paths)
                .take(6)
                .map(|p| p.display().to_string())
                .collect::<Vec<_>>()
                .join(", ")
        });
        let resolved_exists = !tessdata_path.is_empty() && std::path::Path::new(&tessdata_path).exists();

        format!(
            "env={:?} resolved={} exists={} path_preview={:?}",
            env::var("TESSDATA_PREFIX").ok(),
            if tessdata_path.is_empty() {
                "unset"
            } else {
                &tessdata_path
            },
            resolved_exists,
            path_preview
        )
    });

    log_ci_debug(ci_debug_enabled, "tesseract_version", || {
        format!("version={}", TesseractAPI::version())
    });

    validate_language_and_traineddata(&config.language, &tessdata_path)?;

    let api = api_pool.checkout(&tessdata_path, &config.language)?;

    log_ci_debug(ci_debug_enabled, "init", || {
        format!("language={} datapath='{}'", config.language, tessdata_path)
    });

    if ci_debug_enabled {
        match api.get_available_languages() {
            Ok(languages) => {
                log_ci_debug(ci_debug_enabled, "available_languages", move || {
                    let preview = languages.iter().take(10).cloned().collect::<Vec<_>>();
                    format!("count={} preview={:?}", languages.len(), preview)
                });
            }
            Err(err) => {
                log_ci_debug(ci_debug_enabled, "available_languages_error", move || {
                    format!("error={:?}", err)
                });
            }
        }
    }

    let psm_mode = TessPageSegMode::from_int(config.psm as i32);
    let psm_result = api.set_page_seg_mode(psm_mode);
    log_ci_debug(ci_debug_enabled, "set_psm", || match &psm_result {
        Ok(_) => format!("mode={}", config.psm),
        Err(err) => format!("error={:?}", err),
    });
    psm_result.map_err(|e| OcrError::InvalidConfiguration(format!("Failed to set PSM mode: {}", e)))?;

    apply_tesseract_variables(&api, config)?;

    let force_invert_colors = config.preprocessing.as_ref().map(|p| p.invert_colors).unwrap_or(false);

    let processed_pix: Option<xberg_tesseract::Pix> = if prepared_image.apply_pix_preprocessing {
        match xberg_tesseract::Pix::from_raw_rgb(&image_data, width, height) {
            Ok(mut pix) => {
                if let Ok((xres, yres)) = pix.get_resolution()
                    && (xres == 0 || yres == 0)
                {
                    let _ = pix.set_resolution(72, 72);
                }

                let processed = preprocess_pix(pix, force_invert_colors);
                match processed {
                    Ok(p) => Some(p),
                    Err(e) => {
                        tracing::debug!("Leptonica preprocessing failed, using raw image: {}", e);
                        None
                    }
                }
            }
            Err(e) => {
                tracing::debug!("Leptonica Pix creation failed, using raw image: {}", e);
                None
            }
        }
    } else {
        None
    };

    if let Some(ref pix) = processed_pix {
        api.set_image_2(pix.as_ptr())
            .map_err(|e| OcrError::ProcessingFailed(format!("Failed to set preprocessed image: {}", e)))?;
    } else {
        api.set_image(
            &image_data,
            width as i32,
            height as i32,
            bytes_per_pixel as i32,
            bytes_per_line as i32,
        )
        .map_err(|e| OcrError::ProcessingFailed(format!("Failed to set image: {}", e)))?;
    }
    drop(processed_pix);

    let source_dpi = source_dpi.max(70);
    api.set_source_resolution(source_dpi)
        .map_err(|e| OcrError::ProcessingFailed(format!("Failed to set source resolution: {}", e)))?;

    log_ci_debug(ci_debug_enabled, "set_image", || {
        format!(
            "width={} height={} bytes_per_pixel={} bytes_per_line={} source_dpi={}",
            width, height, bytes_per_pixel, bytes_per_line, source_dpi
        )
    });

    // Only (degrees, confidence): the ONNX PP-LCNet orientation classifier used
    // here has no script-detection capability, unlike Tesseract's own
    // `DetectOrientationScript()`/OSD, which this crate does not call (#184).
    #[cfg_attr(not(auto_rotate), allow(unused_mut))]
    let mut detected_orientation: Option<(i32, f32)> = None;
    let auto_rotate_enabled =
        config.preprocessing.as_ref().map(|p| p.auto_rotate).unwrap_or(false) || config.auto_rotate;

    // Recorded into `metadata` below (once `metadata` exists) under the
    // `auto_rotate_unavailable` key, mirroring `pre_formatted` and
    // `word_iterator_skipped_count`: `OcrExtractionResult` has no dedicated
    // warnings field, so `TesseractBackend` reads this key back out and turns
    // it into a `ProcessingWarning` the caller actually sees (#309). Without
    // it, a user's explicit `auto_rotate = true` silently does nothing on a
    // build without the `auto-rotate` feature.
    let auto_rotate_unavailable = is_auto_rotate_requested_but_unavailable(auto_rotate_enabled);

    #[cfg(not(auto_rotate))]
    if auto_rotate_enabled {
        tracing::warn!(
            "auto_rotate requested but the `auto-rotate` feature is not compiled in; skipping orientation detection"
        );
    }

    #[cfg(auto_rotate)]
    if auto_rotate_enabled {
        let orientation_result = image::RgbImage::from_raw(width, height, image_data.clone())
            .ok_or_else(|| crate::error::XbergError::Ocr {
                message: "auto_rotate: image buffer does not match dimensions".to_string(),
                source: None,
            })
            .and_then(|img| doc_orientation_detector().detect(&img));

        match orientation_result {
            Err(e) => {
                tracing::warn!("Orientation detection failed, proceeding without rotation: {}", e);
            }
            Ok(orientation) => {
                let orient_deg = orientation.degrees as i32;
                let orient_conf = orientation.confidence;
                log_ci_debug(ci_debug_enabled, "orientation_detection", || {
                    format!("orientation={}° confidence={:.2}", orient_deg, orient_conf)
                });
                detected_orientation = Some((orient_deg, orient_conf));

                if orient_deg != 0 && orient_conf > MIN_ORIENTATION_CONFIDENCE {
                    tracing::info!(
                        "Auto-rotating image by {} degrees (confidence: {:.2})",
                        orient_deg,
                        orient_conf
                    );

                    let correction_deg = (360 - orient_deg).rem_euclid(360);
                    let (rotated_data, new_width, new_height) =
                        rotate_rgb_image_data(&image_data, width, height, correction_deg);
                    let new_bytes_per_line = new_width * bytes_per_pixel;

                    let rotated_pix = if prepared_image.apply_pix_preprocessing {
                        xberg_tesseract::Pix::from_raw_rgb(&rotated_data, new_width, new_height)
                            .ok()
                            .and_then(|pix| preprocess_pix(pix, force_invert_colors).ok())
                    } else {
                        None
                    };

                    if let Some(ref pix) = rotated_pix {
                        api.set_image_2(pix.as_ptr()).map_err(|e| {
                            OcrError::ProcessingFailed(format!("Failed to set rotated preprocessed image: {}", e))
                        })?;
                    } else {
                        api.set_image(
                            &rotated_data,
                            new_width as i32,
                            new_height as i32,
                            bytes_per_pixel as i32,
                            new_bytes_per_line as i32,
                        )
                        .map_err(|e| OcrError::ProcessingFailed(format!("Failed to set rotated image: {}", e)))?;
                    }

                    drop(rotated_pix);

                    api.set_source_resolution(source_dpi).map_err(|e| {
                        OcrError::ProcessingFailed(format!("Failed to set source resolution after rotation: {}", e))
                    })?;
                    ocr_image_width = new_width;
                    ocr_image_height = new_height;

                    log_ci_debug(ci_debug_enabled, "auto_rotate", || {
                        format!("rotated={}° new_dimensions={}x{}", orient_deg, new_width, new_height)
                    });
                } else {
                    tracing::debug!(
                        degrees = orient_deg,
                        confidence = orient_conf,
                        threshold = MIN_ORIENTATION_CONFIDENCE,
                        "auto_rotate: keeping original orientation"
                    );
                }
            }
        }
    }

    drop(image_data);

    api.recognize()
        .map_err(|e| OcrError::ProcessingFailed(format!("Failed to recognize text: {}", e)))?;

    let mean_text_conf = api.mean_text_conf().unwrap_or(-1);

    log_ci_debug(ci_debug_enabled, "recognize", || {
        format!("completed mean_text_conf={}", mean_text_conf)
    });

    let word_confidence_stats = match api.all_word_confidences() {
        Ok(confidences) if !confidences.is_empty() => {
            let word_count = confidences.len();
            let low_conf_word_count = confidences.iter().filter(|&&c| c < 50).count();

            let mut sorted = confidences.clone();
            sorted.sort_unstable();
            let median_word_conf = if word_count % 2 == 0 {
                (sorted[word_count / 2 - 1] + sorted[word_count / 2]) / 2
            } else {
                sorted[word_count / 2]
            };

            let p10_idx = ((word_count as f64 - 1.0) * 0.1).floor() as usize;
            let p10_word_conf = sorted[p10_idx.min(word_count - 1)];

            Some((median_word_conf, p10_word_conf, word_count, low_conf_word_count))
        }
        Ok(_) => match api.mean_text_conf() {
            Ok(mean_conf) => Some((mean_conf, mean_conf, 0usize, 0usize)),
            Err(_) => None,
        },
        Err(_) => match api.mean_text_conf() {
            Ok(mean_conf) => Some((mean_conf, mean_conf, 0usize, 0usize)),
            Err(_) => None,
        },
    };

    let tsv_data_for_tables = if config.enable_table_detection || config.output_format == "tsv" {
        Some(
            api.get_tsv_text(0)
                .map_err(|e| OcrError::ProcessingFailed(format!("Failed to extract TSV: {}", e)))?,
        )
    } else {
        None
    };

    let mut hocr_document: Option<InternalDocument> = None;

    let (raw_content, mime_type) = match config.output_format.as_str() {
        "text" => {
            let text = api
                .get_utf8_text()
                .map_err(|e| OcrError::ProcessingFailed(format!("Failed to extract text: {}", e)))?;
            (text, "text/plain".to_string())
        }
        "markdown" => {
            let hocr = api
                .get_hocr_text(0)
                .map_err(|e| OcrError::ProcessingFailed(format!("Failed to extract hOCR: {}", e)))?;

            // Per-line dictionary-invalid noise filter (#783). Built here, immediately
            // before parsing, using `hocr_parser::DEFAULT_DICT_INVALID_LINE_RATIO` -- see
            // that constant's doc comment for why this is not yet a configurable
            // `OcrQualityThresholds` field.
            let is_valid_word = |text: &str| match api.is_valid_word(text) {
                Ok(0) => Some(false),
                Ok(_) => Some(true),
                Err(_) => None,
            };
            let dictionary_filter = DictionaryLineFilter {
                is_valid_word: &is_valid_word,
                max_invalid_ratio: crate::ocr::hocr_parser::DEFAULT_DICT_INVALID_LINE_RATIO,
            };

            let internal_doc =
                parse_hocr_to_internal_document_with_page_offset(&hocr, Some(&dictionary_filter), config.page_number);
            let content = flatten_hocr_elements_to_text(&internal_doc.elements);
            hocr_document = Some(internal_doc);

            let mime_type = extraction_config
                .map(|c| match c.output_format {
                    crate::core::config::OutputFormat::Djot => "text/djot",
                    _ => "text/markdown",
                })
                .unwrap_or("text/markdown");

            (content, mime_type.to_string())
        }
        "hocr" => {
            let hocr = api
                .get_hocr_text(0)
                .map_err(|e| OcrError::ProcessingFailed(format!("Failed to extract hOCR: {}", e)))?;
            (hocr, "text/html".to_string())
        }
        "tsv" => {
            let tsv = tsv_data_for_tables
                .as_ref()
                .ok_or_else(|| OcrError::ProcessingFailed("TSV data not available".to_string()))?
                .clone();
            (tsv, "text/plain".to_string())
        }
        _ => {
            return Err(OcrError::InvalidConfiguration(format!(
                "Unsupported output format: {}",
                config.output_format
            )));
        }
    };

    let mut metadata = HashMap::new();
    if let Some(image_preprocessing) = image_preprocessing {
        let value = serde_json::to_value(image_preprocessing).map_err(|error| {
            OcrError::ProcessingFailed(format!("Failed to serialize image preprocessing metadata: {error}"))
        })?;
        metadata.insert(
            crate::ocr_metadata_keys::OCR_IMAGE_PREPROCESSING_METADATA_KEY.to_string(),
            value,
        );
    }
    metadata.insert(
        crate::ocr_metadata_keys::OCR_PROCESSED_IMAGE_WIDTH_METADATA_KEY.to_string(),
        serde_json::Value::Number(ocr_image_width.into()),
    );
    metadata.insert(
        crate::ocr_metadata_keys::OCR_PROCESSED_IMAGE_HEIGHT_METADATA_KEY.to_string(),
        serde_json::Value::Number(ocr_image_height.into()),
    );
    metadata.insert(
        "language".to_string(),
        serde_json::Value::String(config.language.clone()),
    );
    metadata.insert("psm".to_string(), serde_json::Value::String(config.psm.to_string()));
    metadata.insert("table_count".to_string(), serde_json::Value::Number(0.into()));
    metadata.insert("tables_detected".to_string(), serde_json::Value::Number(0.into()));
    if config.output_format == "markdown" {
        metadata.insert(
            "source_format".to_string(),
            serde_json::Value::String("hocr".to_string()),
        );
    }
    if auto_rotate_unavailable {
        metadata.insert("auto_rotate_unavailable".to_string(), serde_json::Value::Bool(true));
    }

    if mean_text_conf >= 0 {
        metadata.insert(
            "mean_text_conf".to_string(),
            serde_json::Value::Number(serde_json::Number::from(mean_text_conf)),
        );
    }

    if let Some((median_conf, p10_conf, word_count, low_conf_count)) = word_confidence_stats {
        metadata.insert(
            "median_word_conf".to_string(),
            serde_json::Value::Number(serde_json::Number::from(median_conf)),
        );
        metadata.insert(
            "p10_word_conf".to_string(),
            serde_json::Value::Number(serde_json::Number::from(p10_conf)),
        );
        metadata.insert(
            "word_count".to_string(),
            serde_json::Value::Number(serde_json::Number::from(word_count)),
        );
        metadata.insert(
            "low_conf_word_count".to_string(),
            serde_json::Value::Number(serde_json::Number::from(low_conf_count)),
        );
    }

    // No `script_name`/`script_confidence` metadata: the ONNX PP-LCNet
    // orientation classifier used here has no script-detection capability,
    // unlike Tesseract's own `DetectOrientationScript()`/OSD (which this crate
    // does not call). Emitting a placeholder script name would misrepresent
    // real detection as having happened (#184). ~keep
    if let Some((orient_deg, orient_conf)) = detected_orientation {
        metadata.insert(
            crate::ocr_metadata_keys::OCR_ORIENTATION_DEGREES_METADATA_KEY.to_string(),
            serde_json::Value::Number(serde_json::Number::from(orient_deg)),
        );
        metadata.insert(
            crate::ocr_metadata_keys::OCR_ORIENTATION_CONFIDENCE_METADATA_KEY.to_string(),
            serde_json::Value::Number(
                serde_json::Number::from_f64(orient_conf as f64).unwrap_or(serde_json::Number::from(0)),
            ),
        );
        if orient_deg != 0 && orient_conf > MIN_ORIENTATION_CONFIDENCE {
            metadata.insert(
                crate::ocr_metadata_keys::OCR_AUTO_ROTATED_METADATA_KEY.to_string(),
                serde_json::Value::Bool(true),
            );
        }
    }

    let mut tables = Vec::new();
    let mut ocr_elements = None;

    if config.enable_table_detection {
        let tsv_data = tsv_data_for_tables.as_ref().unwrap();

        let words = extract_words_from_tsv(tsv_data, config.table_min_confidence)?;
        let regions = cluster_words_into_table_regions(&words);

        for (region_index, region_words) in regions.into_iter().enumerate() {
            if region_words.len() < MIN_TABLE_CANDIDATE_WORDS {
                tracing::debug!(
                    target: "xberg::ocr::tables",
                    region_index,
                    word_count = region_words.len(),
                    min_required = MIN_TABLE_CANDIDATE_WORDS,
                    "OCR table region skipped: below MIN_TABLE_CANDIDATE_WORDS"
                );
                continue;
            }

            let region_left = region_words.iter().map(|w| w.left).min().unwrap_or(0);
            let region_top = region_words.iter().map(|w| w.top).min().unwrap_or(0);
            let region_right = region_words.iter().map(|w| w.left + w.width).max().unwrap_or(0);
            let region_bottom = region_words.iter().map(|w| w.top + w.height).max().unwrap_or(0);
            let word_preview: String = region_words
                .iter()
                .take(12)
                .map(|w| w.text.as_str())
                .collect::<Vec<_>>()
                .join(" ")
                .chars()
                .take(200)
                .collect();

            let table = reconstruct_table(
                &region_words,
                config.table_column_threshold,
                config.table_row_threshold_ratio,
            );

            tracing::debug!(
                target: "xberg::ocr::tables",
                region_index,
                word_count = region_words.len(),
                left = region_left,
                top = region_top,
                right = region_right,
                bottom = region_bottom,
                word_preview = %word_preview,
                raw_rows = table.len(),
                raw_cols = table.first().map_or(0, Vec::len),
                "OCR table region reconstructed"
            );

            if table.is_empty() || table[0].is_empty() {
                continue;
            }

            #[cfg(feature = "pdf")]
            let cleaned = post_process_table(table, false, false);
            #[cfg(not(feature = "pdf"))]
            let cleaned = Some(table);
            let Some(cleaned) = cleaned else {
                continue;
            };

            let markdown_table = table_to_markdown(&cleaned);

            let left = region_words.iter().map(|w| w.left).min().unwrap_or(0);
            let top = region_words.iter().map(|w| w.top).min().unwrap_or(0);
            let right = region_words.iter().map(|w| w.left + w.width).max().unwrap_or(0);
            let bottom = region_words.iter().map(|w| w.top + w.height).max().unwrap_or(0);

            tables.push(OcrTable {
                cells: cleaned,
                markdown: markdown_table,
                page_number: config.page_number,
                bounding_box: Some(OcrTableBoundingBox {
                    left,
                    top,
                    right,
                    bottom,
                }),
            });
        }

        // Real count, not hardcoded to at-most-one (#177): a page can contain
        // several independent tables separated by prose.
        metadata.insert(
            "table_count".to_string(),
            serde_json::Value::Number(tables.len().into()),
        );
        metadata.insert(
            "tables_detected".to_string(),
            serde_json::Value::Number(tables.len().into()),
        );
        if let Some(first) = tables.first() {
            metadata.insert(
                "table_rows".to_string(),
                serde_json::Value::Number(first.cells.len().into()),
            );
            metadata.insert(
                "table_cols".to_string(),
                serde_json::Value::Number(first.cells.first().map_or(0, Vec::len).into()),
            );
        }
    }

    let iterator_extraction = extract_elements_via_iterator(&api, config.page_number, config.min_confidence);
    match iterator_extraction {
        Ok(extraction) if !extraction.elements.is_empty() => {
            insert_word_iterator_skipped_count_metadata(&mut metadata, extraction.skipped_words);
            if extraction.non_text_block_word_count > 0 {
                metadata.insert(
                    "non_text_block_word_count".to_string(),
                    serde_json::Value::Number(extraction.non_text_block_word_count.into()),
                );
            }
            if let Some(ratio) = extraction.dict_invalid_word_ratio {
                metadata.insert(
                    crate::ocr_metadata_keys::OCR_TESSERACT_DICT_INVALID_WORD_RATIO_METADATA_KEY.to_string(),
                    serde_json::Value::Number(
                        serde_json::Number::from_f64(ratio).unwrap_or(serde_json::Number::from(0)),
                    ),
                );
            }
            ocr_elements = Some(extraction.elements);
        }
        _ => {
            if let Some(ref tsv_data) = tsv_data_for_tables {
                let elements = parse_tsv_to_elements(tsv_data, config.min_confidence, config.page_number);
                if !elements.is_empty() {
                    ocr_elements = Some(elements);
                }
            }
        }
    }

    let mut content = strip_control_characters(&raw_content).into_owned();

    let is_markdown_output = extraction_config
        .map(|c| c.output_format == crate::core::config::OutputFormat::Markdown)
        .unwrap_or(config.output_format == "markdown");

    if !tables.is_empty()
        && is_markdown_output
        && let Some(ref tsv_data) = tsv_data_for_tables
    {
        let rebuilt = build_content_with_inline_tables(tsv_data, &tables, config.table_min_confidence);
        if !rebuilt.is_empty() {
            content = rebuilt;
            metadata.insert(
                "pre_formatted".to_string(),
                serde_json::Value::String("markdown".to_string()),
            );
        }
    }

    drop(api);

    Ok(OcrExtractionResult {
        content,
        mime_type,
        metadata,
        tables,
        ocr_elements,
        internal_document: hocr_document,
    })
}

/// Process an image file and return OCR results.
///
/// # Arguments
///
/// * `file_path` - Path to image file
/// * `config` - OCR configuration
/// * `cache` - Cache instance
/// * `output_format` - Optional output format (Plain, Markdown, Djot) for proper mime_type handling
///
/// # Returns
///
/// OCR extraction result
pub(super) fn process_image_file_with_cache(
    file_path: &str,
    config: &TesseractConfig,
    cache: &OcrCache,
    api_pool: &Arc<TesseractApiPool>,
    output_format: Option<crate::core::config::OutputFormat>,
) -> Result<OcrExtractionResult, OcrError> {
    let image_bytes = std::fs::read(file_path)
        .map_err(|e| OcrError::IOError(format!("Failed to read file '{}': {}", file_path, e)))?;
    process_image_with_cache(&image_bytes, config, cache, api_pool, output_format)
}

/// Check if a language value is the "all" wildcard (case-insensitive).
fn is_all_languages(lang: &str) -> bool {
    let lower = lang.to_ascii_lowercase();
    lower == "all" || lower == "*"
}

/// Resolve the "all"/"*" wildcard in a config's language field.
///
/// If the language is a wildcard, scans the tessdata directory for installed
/// languages and returns a new config with the resolved language string.
/// Otherwise returns `None`, indicating the original config should be used as-is.
fn resolve_config_language(config: &TesseractConfig) -> Result<Option<TesseractConfig>, OcrError> {
    if is_all_languages(&config.language) {
        let bootstrap_langs = vec!["eng".to_string()];
        let tessdata_path = resolve_tessdata_path(&bootstrap_langs, config.tessdata_path.as_deref())?;
        let resolved = resolve_all_installed_languages(&tessdata_path)?;
        let mut resolved_config = config.clone();
        resolved_config.language = resolved;
        Ok(Some(resolved_config))
    } else {
        Ok(None)
    }
}

/// Process an image and return OCR results, using cache if enabled.
///
/// Resolves the `"all"` / `"*"` language wildcard, then delegates to
/// [`process_image_resolved`] for caching and OCR execution.
///
/// # Arguments
///
/// * `image_bytes` - Raw image data
/// * `config` - OCR configuration
/// * `cache` - Cache instance
/// * `output_format` - Optional output format (Plain, Markdown, Djot) for proper mime_type handling
///
/// # Returns
///
/// OCR extraction result
pub(super) fn process_image_with_cache(
    image_bytes: &[u8],
    config: &TesseractConfig,
    cache: &OcrCache,
    api_pool: &Arc<TesseractApiPool>,
    output_format: Option<crate::core::config::OutputFormat>,
) -> Result<OcrExtractionResult, OcrError> {
    config.validate().map_err(OcrError::InvalidConfiguration)?;

    let resolved = resolve_config_language(config)?;
    let config = resolved.as_ref().unwrap_or(config);

    process_image_resolved(image_bytes, config, cache, api_pool, output_format)
}

/// Inner implementation operating on an already-resolved config.
///
/// Handles cache lookup, OCR execution, and cache storage. Callers are
/// responsible for validating and resolving wildcards in the config before
/// calling this function.
fn process_image_resolved(
    image_bytes: &[u8],
    config: &TesseractConfig,
    cache: &OcrCache,
    api_pool: &Arc<TesseractApiPool>,
    output_format: Option<crate::core::config::OutputFormat>,
) -> Result<OcrExtractionResult, OcrError> {
    let image_hash = crate::cache::blake3_hash_bytes(image_bytes);

    let config_str = hash_config(config);

    // `output_format` is part of the cache identity: it selects the renderer and
    // therefore the `content` and `mime_type` of the result. Omitting it served a
    // document cached as one format for a request for another (#205).
    if config.use_cache
        && let Some(cached_result) =
            cache.get_cached_result(&image_hash, "tesseract", &config_str, output_format.as_ref())?
    {
        #[cfg(feature = "otel")]
        tracing::Span::current().record("cache.hit", true);
        return Ok(cached_result);
    }

    #[cfg(feature = "otel")]
    tracing::Span::current().record("cache.hit", false);

    let extraction_config = output_format.as_ref().map(|fmt| ExtractionConfig {
        output_format: fmt.clone(),
        ..Default::default()
    });

    let result = perform_ocr(image_bytes, config, api_pool, extraction_config.as_ref())?;

    if config.use_cache
        && let Err(e) = cache.set_cached_result(&image_hash, "tesseract", &config_str, output_format.as_ref(), &result)
    {
        tracing::warn!(error = %e, "Failed to cache the OCR result; the next identical request will re-run OCR");
    }

    Ok(result)
}

/// Process multiple image files in parallel using Rayon.
///
/// Validates and resolves the language wildcard once, then processes all files
/// in parallel using [`process_image_resolved`] directly (skipping redundant
/// per-image resolution).
///
/// Results are returned in the same order as the input file paths.
#[cfg(test)]
pub(super) fn process_image_files_batch(
    file_paths: Vec<String>,
    config: &TesseractConfig,
    cache: &OcrCache,
    api_pool: &Arc<TesseractApiPool>,
) -> Vec<BatchItemResult> {
    #[cfg(not(target_arch = "wasm32"))]
    use rayon::prelude::*;

    if let Err(e) = config.validate().map_err(OcrError::InvalidConfiguration) {
        return file_paths
            .into_iter()
            .map(|path| BatchItemResult {
                file_path: path,
                success: false,
                result: None,
                error: Some(e.to_string()),
            })
            .collect();
    }

    let resolved = match resolve_config_language(config) {
        Ok(r) => r,
        Err(e) => {
            return file_paths
                .into_iter()
                .map(|path| BatchItemResult {
                    file_path: path,
                    success: false,
                    result: None,
                    error: Some(e.to_string()),
                })
                .collect();
        }
    };
    let config = resolved.as_ref().unwrap_or(config);

    #[cfg(not(target_arch = "wasm32"))]
    {
        file_paths
            .par_iter()
            .map(|path| {
                let image_bytes = match std::fs::read(path) {
                    Ok(b) => b,
                    Err(e) => {
                        return BatchItemResult {
                            file_path: path.clone(),
                            success: false,
                            result: None,
                            error: Some(
                                OcrError::IOError(format!("Failed to read file '{}': {}", path, e)).to_string(),
                            ),
                        };
                    }
                };
                match process_image_resolved(&image_bytes, config, cache, api_pool, None) {
                    Ok(result) => BatchItemResult {
                        file_path: path.clone(),
                        success: true,
                        result: Some(result),
                        error: None,
                    },
                    Err(e) => BatchItemResult {
                        file_path: path.clone(),
                        success: false,
                        result: None,
                        error: Some(e.to_string()),
                    },
                }
            })
            .collect()
    }
    #[cfg(target_arch = "wasm32")]
    {
        file_paths
            .iter()
            .map(|path| {
                let image_bytes = match std::fs::read(path) {
                    Ok(b) => b,
                    Err(e) => {
                        return BatchItemResult {
                            file_path: path.clone(),
                            success: false,
                            result: None,
                            error: Some(
                                OcrError::IOError(format!("Failed to read file '{}': {}", path, e)).to_string(),
                            ),
                        };
                    }
                };
                match process_image_resolved(&image_bytes, config, cache, api_pool, None) {
                    Ok(result) => BatchItemResult {
                        file_path: path.clone(),
                        success: true,
                        result: Some(result),
                        error: None,
                    },
                    Err(e) => BatchItemResult {
                        file_path: path.clone(),
                        success: false,
                        result: None,
                        error: Some(e.to_string()),
                    },
                }
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ocr::hocr_parser::HOCR_FONT_SIZE_ATTRIBUTE;
    use tempfile::tempdir;

    /// Exact count: a known number of skipped words must produce exactly the
    /// matching `word_iterator_skipped_count` value, not just a truthy presence
    /// (#192 — catches off-by-one or double-counting regressions).
    #[test]
    fn should_insert_exact_skipped_count_when_words_were_skipped() {
        let mut metadata = HashMap::new();

        insert_word_iterator_skipped_count_metadata(&mut metadata, 3);

        assert_eq!(
            metadata.get("word_iterator_skipped_count"),
            Some(&serde_json::Value::Number(3.into()))
        );
    }

    /// When nothing was skipped, the metadata key must be entirely ABSENT, not
    /// present with a value of `0` — callers rely on key absence as the
    /// clean-extraction signal (#192).
    #[test]
    fn should_omit_skipped_count_key_when_nothing_was_skipped() {
        let mut metadata = HashMap::new();

        insert_word_iterator_skipped_count_metadata(&mut metadata, 0);

        assert!(
            !metadata.contains_key("word_iterator_skipped_count"),
            "key must be absent, not present with value 0"
        );
        assert!(metadata.is_empty());
    }

    /// When `auto_rotate` is not requested, the flag must be `false`
    /// regardless of which build compiled this test (#309).
    #[test]
    fn should_report_auto_rotate_available_when_not_requested() {
        assert!(!is_auto_rotate_requested_but_unavailable(false));
    }

    /// When `auto_rotate` is requested, the flag reflects whether *this* build
    /// has the `auto-rotate` feature compiled in — `true` (unavailable) on a
    /// build without it, `false` (available) on a build with it (#309).
    #[test]
    fn should_report_auto_rotate_unavailable_only_without_the_feature() {
        assert_eq!(is_auto_rotate_requested_but_unavailable(true), cfg!(not(auto_rotate)));
    }

    fn dict_word(text: &str) -> xberg_tesseract::WordData {
        xberg_tesseract::WordData {
            text: text.to_string(),
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
            confidence: 95.0,
            font_attrs: None,
            language: None,
        }
    }

    /// A page dominated by non-dictionary "words" (the LAAALDLI-style noise a scanned
    /// drawing produces) must report a HIGH invalid ratio, not `None` and not a low one.
    #[test]
    fn should_report_high_ratio_when_most_words_are_dictionary_invalid() {
        let words = ["LAAALDLI", "sky", "AEA", "ails", "Bri", "ENT", "FRONT", "ELEVATION"].map(dict_word);
        // Mirror the ordinance_2197 page-13 evidence: only FRONT/ELEVATION are real words.
        let is_valid = |text: &str| Some(matches!(text, "FRONT" | "ELEVATION"));

        let ratio = invalid_word_ratio_from(&words, is_valid).expect("enough candidates to score");

        assert_eq!(
            ratio, 0.75,
            "6 of 8 candidates are dictionary-invalid, expected exactly 0.75"
        );
    }

    /// A page of genuine prose (all real dictionary words) must report a LOW invalid
    /// ratio, not one indistinguishable from the noise case above.
    #[test]
    fn should_report_low_ratio_when_all_words_are_dictionary_valid() {
        let words = ["EXHIBIT", "PLANT", "LIST", "DATE", "November", "Nineteen"].map(dict_word);
        let is_valid = |_: &str| Some(true);

        let ratio = invalid_word_ratio_from(&words, is_valid).expect("enough candidates to score");

        assert_eq!(ratio, 0.0, "an all-valid page must score exactly 0.0, not merely 'low'");
    }

    /// Words below `MIN_WORD_LEN_FOR_DICT_CHECK` or containing non-alphabetic characters
    /// (numbers, punctuation) are never dictionary words regardless of legitimacy, so they
    /// must not count as candidates at all -- scoring them would bias real content toward
    /// "invalid".
    #[test]
    fn should_exclude_short_and_non_alphabetic_words_from_candidates() {
        let words = ["12", "a", "Bo", "3.14", "PLANT", "LIST", "DATE", "November", "Nineteen"].map(dict_word);
        let is_valid = |_: &str| Some(true);

        let ratio = invalid_word_ratio_from(&words, is_valid).expect("enough candidates to score");

        assert_eq!(
            ratio, 0.0,
            "only the 5 alphabetic 3+ char words should count as candidates"
        );
    }

    /// Too few dictionary-checkable words must yield `None`, never `0.0` -- a page this
    /// short is not evidence that "every word is valid".
    #[test]
    fn should_return_none_when_too_few_candidates() {
        let words = ["PLANT", "LIST"].map(dict_word);
        let is_valid = |_: &str| Some(true);

        assert_eq!(
            invalid_word_ratio_from(&words, is_valid),
            None,
            "fewer than MIN_DICT_CANDIDATES_FOR_RATIO checkable words must not produce a ratio"
        );
    }

    /// A failed dictionary lookup must be excluded from both the numerator and the
    /// denominator, not silently counted as invalid (which would bias the ratio) or as
    /// valid (which would hide real noise).
    #[test]
    fn should_exclude_failed_lookups_from_candidates() {
        let words = ["PLANT", "LIST", "DATE", "November", "Nineteen", "BROKEN"].map(dict_word);
        let is_valid = |text: &str| if text == "BROKEN" { None } else { Some(true) };

        let ratio = invalid_word_ratio_from(&words, is_valid).expect("enough candidates to score");

        assert_eq!(ratio, 0.0, "the failed lookup must not count as invalid");
    }

    fn word_at(left: u32, top: u32, width: u32, height: u32, text: &str) -> crate::table_core::HocrWord {
        crate::table_core::HocrWord {
            text: text.to_string(),
            left,
            top,
            width,
            height,
            confidence: 95.0,
        }
    }

    /// Builds a grid of `rows * cols` words starting at `(left, top)`, each
    /// cell `40x20` pixels, simulating one table's worth of OCR words.
    fn table_grid_words(left: u32, top: u32, rows: u32, cols: u32) -> Vec<crate::table_core::HocrWord> {
        let mut words = Vec::new();
        for row in 0..rows {
            for col in 0..cols {
                words.push(word_at(
                    left + col * 60,
                    top + row * 30,
                    40,
                    20,
                    &format!("r{row}c{col}"),
                ));
            }
        }
        words
    }

    #[test]
    fn cluster_words_into_table_regions_separates_two_distant_tables() {
        // Two 3x3 grids of words (9 words each) far apart vertically: a real
        // page with two independent tables separated by a paragraph of prose.
        let mut words = table_grid_words(0, 0, 3, 3);
        words.extend(table_grid_words(0, 1000, 3, 3));

        let regions = cluster_words_into_table_regions(&words);

        assert_eq!(regions.len(), 2, "two spatially distant grids should form two regions");
        assert_eq!(regions[0].len(), 9);
        assert_eq!(regions[1].len(), 9);
    }

    #[test]
    fn cluster_words_into_table_regions_keeps_one_table_as_a_single_region() {
        let words = table_grid_words(0, 0, 4, 3);

        let regions = cluster_words_into_table_regions(&words);

        assert_eq!(
            regions.len(),
            1,
            "a single table's own row gaps must not be split into regions"
        );
        assert_eq!(regions[0].len(), 12);
    }

    #[test]
    fn cluster_words_into_table_regions_empty_input_yields_no_regions() {
        assert!(cluster_words_into_table_regions(&[]).is_empty());
    }

    fn text_element_with_font_size(text: &str, font_size: Option<f64>) -> crate::types::internal::InternalElement {
        let mut elem = crate::types::internal::InternalElement::text(
            ElementKind::OcrText {
                level: crate::types::OcrElementLevel::Block,
            },
            text,
            0,
        );
        if let Some(size) = font_size {
            elem.attributes
                .get_or_insert_with(Default::default)
                .insert(HOCR_FONT_SIZE_ATTRIBUTE.to_string(), size.to_string());
        }
        elem
    }

    #[test]
    fn flatten_hocr_elements_to_text_never_bakes_heading_syntax_regardless_of_font_size() {
        // Regression test for the `Plain`-format markdown leak: a large-font,
        // short, single-line paragraph used to be promoted to a `## ` heading
        // right in this string, and `OcrExtractionResult::content` (built
        // from this string) is consumed unrendered by some callers, so the
        // markdown syntax leaked into `Plain` output. This must fail against
        // the old behavior, which produced
        // "## Chapter One\n\nBody text at normal size." instead.
        let elements = vec![
            text_element_with_font_size("Chapter One", Some(28.0)),
            text_element_with_font_size("Body text at normal size.", Some(12.0)),
        ];

        let flattened = flatten_hocr_elements_to_text(&elements);

        assert_eq!(flattened, "Chapter One\n\nBody text at normal size.");
        assert!(
            !flattened.contains('#'),
            "flattened OCR text must contain no markdown heading syntax: {flattened:?}"
        );
    }

    #[test]
    fn flatten_hocr_elements_to_text_leaves_content_unchanged_without_font_sizes() {
        let elements = vec![
            text_element_with_font_size("First paragraph.", None),
            text_element_with_font_size("Second paragraph.", None),
        ];

        let markdown = flatten_hocr_elements_to_text(&elements);

        assert_eq!(markdown, "First paragraph.\n\nSecond paragraph.");
    }

    #[test]
    fn test_is_all_languages() {
        assert!(is_all_languages("all"));
        assert!(is_all_languages("ALL"));
        assert!(is_all_languages("All"));
        assert!(is_all_languages("*"));
        assert!(!is_all_languages("eng"));
        assert!(!is_all_languages("eng+fra"));
        assert!(!is_all_languages(""));
    }

    #[test]
    fn test_resolve_config_language_passthrough() {
        let config = TesseractConfig {
            language: "eng".to_string(),
            ..TesseractConfig::default()
        };
        let resolved = resolve_config_language(&config).unwrap();
        assert!(resolved.is_none(), "non-wildcard should return None (no clone)");
    }

    #[test]
    fn test_compute_image_hash_deterministic() {
        let image_bytes = vec![1, 2, 3, 4, 5];

        let hash1 = crate::cache::blake3_hash_bytes(&image_bytes);
        let hash2 = crate::cache::blake3_hash_bytes(&image_bytes);

        assert_eq!(hash1, hash2);
        assert_eq!(hash1.len(), 32);
    }

    #[test]
    fn test_compute_image_hash_different_data() {
        let image_bytes1 = vec![1, 2, 3, 4, 5];
        let image_bytes2 = vec![5, 4, 3, 2, 1];

        let hash1 = crate::cache::blake3_hash_bytes(&image_bytes1);
        let hash2 = crate::cache::blake3_hash_bytes(&image_bytes2);

        assert_ne!(hash1, hash2);
    }

    #[test]
    fn test_log_ci_debug_disabled() {
        log_ci_debug(false, "test_stage", || "test message".to_string());
    }

    #[test]
    fn test_log_ci_debug_enabled() {
        log_ci_debug(true, "test_stage", || "test message".to_string());
    }

    #[test]
    fn test_process_image_file_nonexistent() {
        let temp_dir = tempdir().unwrap();
        let cache = OcrCache::new(Some(temp_dir.path().to_path_buf())).unwrap();
        let config = TesseractConfig {
            output_format: "text".to_string(),
            enable_table_detection: false,
            use_cache: false,
            ..TesseractConfig::default()
        };

        let api_pool = TesseractApiPool::new();
        let result = process_image_file_with_cache("/nonexistent/file.png", &config, &cache, &api_pool, None);
        assert!(result.is_err());
        assert!(result.unwrap_err().to_string().contains("Failed to read file"));
    }

    #[test]
    fn test_process_image_invalid_image_data() {
        let temp_dir = tempdir().unwrap();
        let cache = OcrCache::new(Some(temp_dir.path().to_path_buf())).unwrap();
        let config = TesseractConfig {
            output_format: "text".to_string(),
            enable_table_detection: false,
            use_cache: false,
            ..TesseractConfig::default()
        };

        let invalid_data = vec![0, 1, 2, 3, 4];
        let api_pool = TesseractApiPool::new();
        let result = process_image_with_cache(&invalid_data, &config, &cache, &api_pool, None);

        assert!(result.is_err());
    }

    /// Builds a tiny valid PNG so `process_image_with_cache` runs Tesseract on real
    /// (if content-free) image bytes instead of erroring out on invalid data.
    fn tiny_test_png_bytes() -> Vec<u8> {
        use image::{ImageBuffer, ImageFormat, Rgb, RgbImage};
        use std::io::Cursor;

        let img: RgbImage = ImageBuffer::from_pixel(32, 32, Rgb([255, 255, 255]));
        let mut bytes: Vec<u8> = Vec::new();
        img.write_to(&mut Cursor::new(&mut bytes), ImageFormat::Png).unwrap();
        bytes
    }

    /// Regression test for #687 PART 2: `TesseractConfig::use_cache = false` must be an
    /// end-to-end bypass, not just a lookup skip — it must neither read a pre-existing
    /// entry nor write a new one. Uses a real (non-mocked) `TesseractAPI`, matching the
    /// pattern of `config::tests::test_apply_tesseract_variables_enables_hocr_font_info`:
    /// gracefully skips when no Tesseract/tessdata is available in this environment.
    ///
    /// This exercises a genuinely new code path (an explicit cache pre-seed followed by a
    /// `use_cache: false` call) that has no equivalent before this change, so there is
    /// nothing "unfixed" to run it against — the bypass plumbing this proves either exists
    /// or the test cannot be written.
    #[test]
    fn process_image_with_cache_does_not_read_or_write_the_cache_when_use_cache_is_false() {
        let api = match xberg_tesseract::TesseractAPI::new() {
            Ok(api) => api,
            Err(_) => return, // no Tesseract/Leptonica available in this environment
        };
        if api.init("", "eng").is_err() {
            return; // no "eng" tessdata available in this environment
        }

        let temp_dir = tempdir().unwrap();
        let cache = OcrCache::new(Some(temp_dir.path().to_path_buf())).unwrap();

        let image_bytes = tiny_test_png_bytes();
        let config = TesseractConfig {
            output_format: "text".to_string(),
            enable_table_detection: false,
            use_cache: false,
            ..TesseractConfig::default()
        };

        // Pre-seed the exact entry `process_image_resolved` would look up if caching were
        // enabled, carrying an obviously-wrong marker. If the bypass ever regresses into a
        // read, this is what would come back instead of a fresh OCR result.
        let image_hash = crate::cache::blake3_hash_bytes(&image_bytes);
        let config_str = hash_config(&config);
        let marker = OcrExtractionResult {
            content: "STALE MARKER: use_cache=false must never return this".to_string(),
            mime_type: "text/plain".to_string(),
            metadata: HashMap::new(),
            tables: Vec::new(),
            ocr_elements: None,
            internal_document: None,
        };
        cache
            .set_cached_result(&image_hash, "tesseract", &config_str, None, &marker)
            .unwrap();

        let api_pool = TesseractApiPool::new();
        let result = process_image_with_cache(&image_bytes, &config, &cache, &api_pool, None)
            .expect("OCR on a valid image must succeed");

        assert_ne!(
            result.content, marker.content,
            "use_cache=false must not read the pre-seeded cache entry"
        );

        let stats = cache.get_stats().unwrap();
        assert_eq!(
            stats.total_files, 1,
            "use_cache=false must not write a new cache entry (only the pre-seeded marker file may exist)"
        );
    }

    #[test]
    fn test_preprocess_pix_produces_grayscale_with_valid_resolution() {
        let width = 32;
        let height = 32;
        let mut rgb_data = Vec::with_capacity((width * height * 3) as usize);
        for y in 0..height {
            for x in 0..width {
                let value = if (x + y) % 2 == 0 { 32 } else { 224 };
                rgb_data.extend_from_slice(&[value, value, value]);
            }
        }
        let mut pix = xberg_tesseract::Pix::from_raw_rgb(&rgb_data, width, height).unwrap();
        pix.set_resolution(300, 300).unwrap();

        let processed = preprocess_pix(pix, false).unwrap();

        assert_eq!(processed.width(), width as i32);
        assert_eq!(processed.height(), height as i32);
        assert_eq!(processed.depth(), 8);
        assert_eq!(processed.get_resolution().unwrap(), (72, 72));
    }

    #[test]
    fn test_prepare_ocr_image_without_config_preserves_shadowed_rgb() {
        let rgb_data = vec![0, 1, 2, 3, 4, 5];

        let prepared = prepare_ocr_image(rgb_data.clone(), 2, 1, None, None, false, None);

        assert_eq!(prepared.data, rgb_data);
        assert_eq!(prepared.width, 2);
        assert_eq!(prepared.height, 1);
        assert_eq!(prepared.source_dpi, RAW_IMAGE_SOURCE_DPI);
        assert!(!prepared.apply_pix_preprocessing);
    }

    #[test]
    fn test_clean_white_rgb_selects_default_preprocessing() {
        const SAMPLE_PIXEL_COUNT: usize = 16;
        let rgb_data = vec![u8::MAX; SAMPLE_PIXEL_COUNT * RGB_CHANNEL_COUNT];

        assert!(should_apply_default_preprocessing(&rgb_data));
    }

    #[test]
    fn test_prepare_ocr_image_without_config_preprocesses_clean_white_rgb() {
        const WIDTH: u32 = 4;
        const HEIGHT: u32 = 4;
        let rgb_data = vec![u8::MAX; WIDTH as usize * HEIGHT as usize * RGB_CHANNEL_COUNT];

        let prepared = prepare_ocr_image(rgb_data, WIDTH, HEIGHT, None, None, false, None);

        assert!(prepared.apply_pix_preprocessing);
    }

    #[test]
    fn test_shadowed_rgb_skips_default_preprocessing() {
        const SAMPLE_PIXEL_COUNT: usize = 16;
        const SHADOWED_CHANNEL_VALUE: u8 = 128;
        let rgb_data = vec![SHADOWED_CHANNEL_VALUE; SAMPLE_PIXEL_COUNT * RGB_CHANNEL_COUNT];

        assert!(!should_apply_default_preprocessing(&rgb_data));
    }

    #[test]
    fn test_prepare_ocr_image_with_config_keeps_preprocessing_enabled() {
        let rgb_data = vec![255; 12];
        let preprocessing = crate::types::ImagePreprocessingConfig {
            target_dpi: 72,
            ..Default::default()
        };

        let prepared = prepare_ocr_image(rgb_data, 2, 2, Some(&preprocessing), None, false, None);

        assert!(prepared.apply_pix_preprocessing);
    }

    /// #209: when a caller supplies `ImageExtractionConfig`, its `max_image_dimension`
    /// and `auto_adjust_dpi` must actually reach the DPI-normalization step instead of
    /// being silently replaced by `ImageDpiConfig::default()`.
    #[test]
    fn test_prepare_preprocessed_ocr_image_honours_image_extraction_config_limits() {
        const SOURCE_DIMENSION: u32 = 4;
        let rgb_data = vec![255; SOURCE_DIMENSION as usize * SOURCE_DIMENSION as usize * RGB_CHANNEL_COUNT];
        let preprocessing = crate::types::ImagePreprocessingConfig {
            target_dpi: 300,
            ..Default::default()
        };
        let images_config = crate::core::config::ImageExtractionConfig {
            max_image_dimension: 2,
            auto_adjust_dpi: false,
            ..Default::default()
        };

        let prepared = prepare_preprocessed_ocr_image(
            rgb_data,
            SOURCE_DIMENSION,
            SOURCE_DIMENSION,
            &preprocessing,
            Some(&images_config),
            false,
            None,
        );

        assert_eq!(prepared.width, 2, "max_image_dimension=2 must clamp the resized width");
        assert_eq!(
            prepared.height, 2,
            "max_image_dimension=2 must clamp the resized height"
        );
        let metadata = prepared
            .image_preprocessing
            .expect("successful normalization must retain its metadata");
        assert_eq!(metadata.original_dimensions.width, SOURCE_DIMENSION as usize);
        assert_eq!(metadata.original_dimensions.height, SOURCE_DIMENSION as usize);
        assert_eq!(
            metadata.new_dimensions.as_ref().map(|dimensions| dimensions.width),
            Some(2)
        );
        assert_eq!(
            metadata.new_dimensions.as_ref().map(|dimensions| dimensions.height),
            Some(2)
        );
        assert!(metadata.dimension_clamped);
    }

    /// Pixel width of a US Letter page (612pt wide) rendered at the 150 DPI the PDF OCR route
    /// asks `render_page_with_safeguards` for.
    const LETTER_AT_150_DPI_WIDTH_PX: u32 = 1275;
    /// Pixel height of the same page (792pt tall) at 150 DPI.
    const LETTER_AT_150_DPI_HEIGHT_PX: u32 = 1650;

    /// A clean white Letter-sized raster, the shape the PDF OCR route actually hands over: it
    /// passes `should_apply_default_preprocessing`, so both the explicit and the implicit
    /// preprocessing branch reach DPI normalization.
    fn letter_page_raster_at_150_dpi() -> Vec<u8> {
        vec![u8::MAX; LETTER_AT_150_DPI_WIDTH_PX as usize * LETTER_AT_150_DPI_HEIGHT_PX as usize * RGB_CHANNEL_COUNT]
    }

    /// The defect: a page rendered at 150 DPI was normalized as if it were 72 DPI, so the scale
    /// factor became `target_dpi / 72` instead of `target_dpi / 150`.
    ///
    /// With the real 150 handed in, `calculate_smart_dpi` sees a 8.5x11in page (612x792pt),
    /// finds 300 DPI fits inside `max_image_dimension` (11in * 300 = 3300px <= 4096), and
    /// returns the full 300. The resize is then a clean 2.0x to exactly 2550x3300 with no
    /// dimension clamp, and Tesseract is told 300 — which is what the raster now is.
    ///
    /// Fails against unfixed code: `prepare_ocr_image` has no `known_source_dpi` parameter at
    /// all there, so this does not compile. Restoring the old six-argument call (dropping the
    /// `Some(150.0)`) makes it compile and then fail on the first assertion, because the 72
    /// assumption yields `final_dpi = 179` and a 3165x4096 clamped raster:
    /// `assertion \`left == right\` failed: left: 4096, right: 3300`.
    #[test]
    fn should_honour_known_source_dpi_instead_of_assuming_72() {
        const KNOWN_RENDER_DPI: f64 = 150.0;
        const EXPECTED_WIDTH_PX: u32 = 2550;
        const EXPECTED_HEIGHT_PX: u32 = 3300;
        const EXPECTED_SOURCE_DPI: i32 = 300;

        let prepared = prepare_ocr_image(
            letter_page_raster_at_150_dpi(),
            LETTER_AT_150_DPI_WIDTH_PX,
            LETTER_AT_150_DPI_HEIGHT_PX,
            Some(&crate::types::ImagePreprocessingConfig::default()),
            None,
            false,
            Some(KNOWN_RENDER_DPI),
        );

        assert_eq!(
            prepared.height, EXPECTED_HEIGHT_PX,
            "a 150 DPI Letter page scaled to the 300 DPI target is 3300px tall, not the 4096px \
             the max_image_dimension clamp produces when the source is mistaken for 72 DPI"
        );
        assert_eq!(
            prepared.width, EXPECTED_WIDTH_PX,
            "the matching width for a clean 2.0x resize"
        );
        assert_eq!(
            prepared.source_dpi, EXPECTED_SOURCE_DPI,
            "Tesseract must be told the raster's real resolution, not the 179 the 72 assumption \
             derives"
        );
    }

    /// The contrast leg, pinning the arithmetic the defect produced so a regression is visible
    /// rather than merely different: with no known source DPI the 72 assumption still applies,
    /// the smart-DPI step derives 179 from an apparent 17.7x22.9in page, and the resize runs
    /// into the 4096px `max_image_dimension` clamp.
    ///
    /// Fails against unfixed code only by not compiling (the seventh argument does not exist);
    /// its values are identical either way, which is the point — this leg proves the fix changed
    /// nothing for callers that do not supply a DPI.
    #[test]
    fn should_keep_assuming_72_dpi_when_source_dpi_is_unknown() {
        const CLAMPED_DIMENSION_PX: u32 = 4096;
        const DERIVED_SOURCE_DPI: i32 = 179;

        let prepared = prepare_ocr_image(
            letter_page_raster_at_150_dpi(),
            LETTER_AT_150_DPI_WIDTH_PX,
            LETTER_AT_150_DPI_HEIGHT_PX,
            Some(&crate::types::ImagePreprocessingConfig::default()),
            None,
            false,
            None,
        );

        assert_eq!(
            prepared.height, CLAMPED_DIMENSION_PX,
            "without a known source DPI the 72 assumption drives the resize into the clamp"
        );
        assert_eq!(
            prepared.source_dpi, DERIVED_SOURCE_DPI,
            "and reports the DPI that assumption implies"
        );
    }

    /// A raw image the caller knows nothing about still gets the 72 fallback on the
    /// pass-through branch, so standalone image OCR is untouched by the new parameter.
    ///
    /// Fails against unfixed code by not compiling; behaviourally it is the pre-existing
    /// contract, asserted here so the new parameter cannot quietly displace it.
    #[test]
    fn should_default_to_72_dpi_for_unpreprocessed_raw_image_without_known_dpi() {
        let rgb_data = vec![0, 1, 2, 3, 4, 5];

        let prepared = prepare_ocr_image(rgb_data.clone(), 2, 1, None, None, false, None);

        assert_eq!(prepared.data, rgb_data, "the raster must pass through untouched");
        assert_eq!(prepared.source_dpi, RAW_IMAGE_SOURCE_DPI);
    }

    /// The same pass-through branch reports a known resolution verbatim rather than the 72
    /// fallback: the bytes are unchanged, so their resolution is whatever the caller measured.
    ///
    /// Fails against unfixed code: there is no way to express this at all — `prepare_ocr_image`
    /// hardcodes `RAW_IMAGE_SOURCE_DPI` on this branch, so the reported DPI is 72 and the
    /// assertion reads `assertion \`left == right\` failed: left: 72, right: 150`.
    #[test]
    fn should_report_known_source_dpi_on_the_unpreprocessed_branch() {
        const KNOWN_RENDER_DPI: f64 = 150.0;
        let rgb_data = vec![0, 1, 2, 3, 4, 5];

        let prepared = prepare_ocr_image(rgb_data, 2, 1, None, None, false, Some(KNOWN_RENDER_DPI));

        assert_eq!(prepared.source_dpi, 150);
        assert!(!prepared.apply_pix_preprocessing);
    }

    #[test]
    fn test_should_invert_for_polarity_dark_background_with_text() {
        // Dark background (mean well below threshold) with a clear light-text
        // fraction should trigger auto-detected inversion.
        assert!(should_invert_for_polarity(40.0, 0.05, false));
    }

    #[test]
    fn test_should_invert_for_polarity_light_background_not_inverted() {
        // Typical dark-text-on-light-paper scan: high mean, no auto-invert.
        assert!(!should_invert_for_polarity(220.0, 0.9, false));
    }

    #[test]
    fn test_should_invert_for_polarity_uniformly_dark_image_not_inverted() {
        // Dark mean but essentially no light pixels: a uniformly dark, non-text
        // image (e.g. a dark photo) should NOT be auto-inverted.
        assert!(!should_invert_for_polarity(20.0, 0.0, false));
    }

    #[test]
    fn test_should_invert_for_polarity_force_true_overrides_light_background() {
        // Explicit invert_colors=true forces inversion even on a light-mean image.
        assert!(should_invert_for_polarity(220.0, 0.9, true));
    }

    #[test]
    fn test_should_invert_for_polarity_force_false_still_auto_detects() {
        assert!(should_invert_for_polarity(10.0, 0.5, false));
    }

    #[test]
    fn test_preprocess_pix_inverts_light_on_dark_image() {
        // Synthetic light-text-on-dark-background image: dark background (20)
        // with a bright "text" region covering ~6% of the image.
        let width = 40u32;
        let height = 40u32;
        let mut rgb_data = Vec::with_capacity((width * height * 3) as usize);
        for y in 0..height {
            for x in 0..width {
                let is_text = (10..13).contains(&y) && (5..25).contains(&x);
                let value = if is_text { 230u8 } else { 20u8 };
                rgb_data.extend_from_slice(&[value, value, value]);
            }
        }

        let pix = xberg_tesseract::Pix::from_raw_rgb(&rgb_data, width, height).unwrap();
        let original_gray = pix.to_grayscale().unwrap();
        let (original_mean, _) = original_gray
            .grayscale_stats(LIGHT_PIXEL_VALUE_THRESHOLD, POLARITY_SAMPLE_STRIDE)
            .unwrap();
        drop(original_gray);

        // Auto-detection with no explicit override should invert: the result's
        // background (post background_normalize + grayscale) should be light,
        // i.e. brighter on average than the raw dark-background source.
        let processed = preprocess_pix(pix, false).unwrap();
        let (processed_mean, _) = processed
            .grayscale_stats(LIGHT_PIXEL_VALUE_THRESHOLD, POLARITY_SAMPLE_STRIDE)
            .unwrap();

        assert!(
            processed_mean > original_mean,
            "expected inverted+normalized output to be brighter than the dark-background \
             source (original_mean={original_mean}, processed_mean={processed_mean})"
        );
    }

    #[test]
    fn test_preprocess_pix_does_not_invert_dark_on_light_image() {
        // A normal dark-text-on-light-paper image should not be inverted:
        // processing should keep (or increase) brightness, not flip polarity.
        let width = 40u32;
        let height = 40u32;
        let mut rgb_data = Vec::with_capacity((width * height * 3) as usize);
        for y in 0..height {
            for x in 0..width {
                let is_text = (10..13).contains(&y) && (5..25).contains(&x);
                let value = if is_text { 20u8 } else { 230u8 };
                rgb_data.extend_from_slice(&[value, value, value]);
            }
        }

        let pix = xberg_tesseract::Pix::from_raw_rgb(&rgb_data, width, height).unwrap();
        let processed = preprocess_pix(pix, false).unwrap();
        let (processed_mean, _) = processed
            .grayscale_stats(LIGHT_PIXEL_VALUE_THRESHOLD, POLARITY_SAMPLE_STRIDE)
            .unwrap();

        assert!(
            processed_mean > DARK_BACKGROUND_MEAN_THRESHOLD,
            "expected light-background image to stay light after preprocessing (mean={processed_mean})"
        );
    }

    #[cfg(auto_rotate)]
    #[test]
    #[cfg(auto_rotate)]
    fn test_rotate_rgb_image_data_identity() {
        let data: Vec<u8> = (0..18).collect();
        let (out, w, h) = rotate_rgb_image_data(&data, 2, 3, 0);
        assert_eq!(out, data);
        assert_eq!(w, 2);
        assert_eq!(h, 3);
    }

    #[cfg(auto_rotate)]
    #[test]
    #[cfg(auto_rotate)]
    fn test_rotate_rgb_image_data_180() {
        let data = vec![1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        let (out, w, h) = rotate_rgb_image_data(&data, 2, 2, 180);
        assert_eq!(w, 2);
        assert_eq!(h, 2);
        assert_eq!(out, vec![10, 11, 12, 7, 8, 9, 4, 5, 6, 1, 2, 3]);
    }

    #[cfg(auto_rotate)]
    #[test]
    #[cfg(auto_rotate)]
    fn test_rotate_rgb_image_data_90_swaps_dimensions() {
        let data: Vec<u8> = (0..18).collect();
        let (_, w, h) = rotate_rgb_image_data(&data, 2, 3, 90);
        assert_eq!(w, 3);
        assert_eq!(h, 2);
    }

    #[cfg(auto_rotate)]
    #[test]
    #[cfg(auto_rotate)]
    fn test_rotate_rgb_image_data_270_swaps_dimensions() {
        let data: Vec<u8> = (0..18).collect();
        let (_, w, h) = rotate_rgb_image_data(&data, 2, 3, 270);
        assert_eq!(w, 3);
        assert_eq!(h, 2);
    }

    #[cfg(auto_rotate)]
    #[test]
    #[cfg(auto_rotate)]
    fn test_rotate_rgb_image_data_90_then_270_is_identity() {
        let data: Vec<u8> = (0..18).collect();
        let (rotated_90, w1, h1) = rotate_rgb_image_data(&data, 2, 3, 90);
        let (back, w2, h2) = rotate_rgb_image_data(&rotated_90, w1, h1, 270);
        assert_eq!(w2, 2);
        assert_eq!(h2, 3);
        assert_eq!(back, data);
    }

    #[cfg(auto_rotate)]
    #[test]
    #[cfg(auto_rotate)]
    fn test_rotate_rgb_image_data_unsupported_angle() {
        let data: Vec<u8> = (0..12).collect();
        let (out, w, h) = rotate_rgb_image_data(&data, 2, 2, 45);
        assert_eq!(out, data);
        assert_eq!(w, 2);
        assert_eq!(h, 2);
    }
}
