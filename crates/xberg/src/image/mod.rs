/// DPI detection and normalization for scanned images.
pub mod dpi;
/// Image preprocessing pipeline: denoising, deskew, binarization, rotation.
pub mod preprocessing;
/// Image resize helpers used before OCR to normalize resolution.
pub mod resize;

// Re-exported only for the Tesseract processor (`ocr::processor::execution`); the
// standalone-image path calls `preprocessing::normalize_image_dpi_owned` by full path,
// so under `ocr-pipeline` alone this alias has no consumer.
#[cfg(feature = "ocr")]
pub(crate) use preprocessing::normalize_image_dpi_owned;
