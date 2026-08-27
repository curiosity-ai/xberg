//! Regression tests for xberg-io/xberg#1223: `PdfConfig.passwords` must be
//! honored for encrypted PDFs, and an un-openable encrypted PDF must error
//! rather than silently return empty content.

#![cfg(feature = "pdf")]

mod helpers;
use helpers::extract_bytes_document_blocking;

use xberg::core::config::{ExtractionConfig, PdfConfig};

const PDF_MIME: &str = "application/pdf";

/// A committed encrypted single-page PDF, protected by user password
/// "secret". Shared with `xberg-native-pdf`'s own encryption tests
/// (`crates/xberg-native-pdf/tests/fixtures/encrypted_needs_password.pdf`,
/// see `document.rs::test_encrypted_pdf_works_after_authentication`).
///
/// Previously this test built a fresh AES-256 encrypted PDF at runtime via
/// `xberg_native_pdf::writer::DocumentBuilder::save_encrypted`. That API
/// went away with the PDF writer; hand-rolling an AES-256 R6 encryption
/// (ISO 32000-2 Algorithm 2.A/2.B key derivation) is not something that can
/// be done reliably without a way to verify it, so this reuses the
/// already-encrypted, already-verified fixture instead.
fn encrypted_pdf() -> Vec<u8> {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/pdf/encrypted_needs_password.pdf");
    std::fs::read(&path).unwrap_or_else(|e| panic!("read {path:?}: {e}"))
}

fn config_with_passwords(passwords: Vec<String>) -> ExtractionConfig {
    ExtractionConfig {
        pdf_options: Some(PdfConfig {
            passwords: if passwords.is_empty() { None } else { Some(passwords) },
            ..PdfConfig::default()
        }),
        ..ExtractionConfig::default()
    }
}

#[test]
fn correct_password_authenticates_and_does_not_error() {
    let bytes = encrypted_pdf();
    let config = config_with_passwords(vec!["secret".to_string()]);
    // erroring on the encrypted document. NOTE: recovering the *decrypted text*
    let result = extract_bytes_document_blocking(&bytes, PDF_MIME, &config);
    assert!(
        result.is_ok(),
        "the correct password must authenticate and not error; got: {:?}",
        result.err()
    );
}

#[test]
fn missing_password_errors_not_empty() {
    let bytes = encrypted_pdf();
    let config = config_with_passwords(vec![]);
    let result = extract_bytes_document_blocking(&bytes, PDF_MIME, &config);
    assert!(
        result.is_err(),
        "an encrypted PDF with no password must error, not return empty content; got: {:?}",
        result.map(|d| d.content)
    );
}

#[test]
fn wrong_password_errors() {
    let bytes = encrypted_pdf();
    let config = config_with_passwords(vec!["not-the-password".to_string()]);
    let result = extract_bytes_document_blocking(&bytes, PDF_MIME, &config);
    assert!(
        result.is_err(),
        "a wrong password must error; got: {:?}",
        result.map(|d| d.content)
    );
}
