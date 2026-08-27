//! Regression tests for two release-reachable defects in the stream-decoding path:
//!
//! 1. `DecodeParams` (built from an attacker-controlled `/DecodeParms` dictionary via
//!    `as_integer().unwrap_or(n) as usize`, with zero range validation) could drive
//!    `decode_predictor` into an out-of-bounds slice index or a `data.chunks(0)` panic.
//!    See `xberg_native_pdf::decoders::DecodeParams::checked_pixel_bytes_per_row` /
//!    `checked_bytes_per_row`, and their use in `decode_predictor`.
//! 2. The production filter chain (`decode_stream_with_params`, the function every real
//!    stream-decoding call site uses) had no decompression-ratio or output-size guard, so a
//!    short chain of uncapped filters (e.g. repeated `/RunLengthDecode`) could expand a few
//!    bytes of input by orders of magnitude. `decode_stream_with_params` now delegates to
//!    `decode_stream_with_options`, which enforces the default 100:1 ratio / 100 MB cap.
//!
//! Every panic-reproduction test here is paired with a positive control proving the fix
//! didn't tighten the guard into rejecting ordinary, well-formed streams.

use bytes::Bytes;
use flate2::Compression;
use flate2::write::ZlibEncoder;
use std::collections::HashMap;
use std::io::Write;
use xberg_native_pdf::decoders::{DecodeParams, decode_predictor, decode_stream_with_params};
use xberg_native_pdf::object::Object;

/// Build an `Object::Stream` with a `/Filter` and an optional `/DecodeParms` dictionary,
/// mirroring how `object.rs::extract_decode_params` receives its input in production.
fn stream_with_filter_and_decode_parms(
    data: &'static [u8],
    filter: &str,
    decode_parms: Option<HashMap<String, Object>>,
) -> Object {
    let mut dict = HashMap::new();
    dict.insert("Filter".to_string(), Object::Name(filter.to_string()));
    if let Some(parms) = decode_parms {
        dict.insert("DecodeParms".to_string(), Object::Dictionary(parms));
    }
    Object::Stream {
        dict,
        data: Bytes::from_static(data),
    }
}

fn decode_parms_dict(predictor: i64, columns: i64, colors: i64, bits_per_component: i64) -> HashMap<String, Object> {
    let mut parms = HashMap::new();
    parms.insert("Predictor".to_string(), Object::Integer(predictor));
    parms.insert("Columns".to_string(), Object::Integer(columns));
    parms.insert("Colors".to_string(), Object::Integer(colors));
    parms.insert("BitsPerComponent".to_string(), Object::Integer(bits_per_component));
    parms
}

// ---------------------------------------------------------------------------
// Defect 1, Panic A: /Colors wider than the row implies an out-of-bounds index.
// ---------------------------------------------------------------------------

/// Direct reproduction: `/Predictor 2 /Colors 4 /BitsPerComponent 1 /Columns 1` makes
/// `bytes_per_row == 1` but the TIFF-predictor loop unconditionally copies `colors` (4)
/// leading bytes out of a 1-byte row. Before the fix this indexed `row_data[1]` on a
/// 1-byte slice and panicked; it must now return `Err`.
///
/// Fails if: `decode_predictor` panics (test process aborts), or returns `Ok` (silently
/// accepting a declaration it cannot honor).
#[test]
fn tiff_predictor_colors_wider_than_row_returns_err_not_panic() {
    let params = DecodeParams {
        predictor: 2,
        columns: 1,
        colors: 4,
        bits_per_component: 1,
    };
    let data = vec![0u8]; // exactly one row per the (correct) 1-byte row size
    let result = decode_predictor(&data, &params);
    assert!(
        result.is_err(),
        "Colors=4 cannot fit in a 1-byte row (Columns=1, BitsPerComponent=1); expected Err, got {result:?}"
    );
}

/// Same defect, exercised through the full production path: `Object::decode_stream_data`
/// -> `decoders::decode_stream_with_params` -> `decode_predictor`, with the DecodeParms
/// dictionary parsed by `object.rs::extract_decode_params` exactly as a real PDF stream
/// would supply it.
///
/// Fails if: the call panics, or returns `Ok` instead of a decode error.
#[test]
fn object_stream_with_colors_wider_than_row_returns_err_not_panic() {
    let stream = stream_with_filter_and_decode_parms(b"00", "ASCIIHexDecode", Some(decode_parms_dict(2, 1, 4, 1)));
    let result = stream.decode_stream_data();
    assert!(
        result.is_err(),
        "malformed /Colors vs row width must error out through the Object path, got {result:?}"
    );
}

// ---------------------------------------------------------------------------
// Defect 1, Panic B: /Columns 0 on empty data passes the multiple-of-0 guard,
// then `data.chunks(0)` panics ("chunk size must be non-zero").
// ---------------------------------------------------------------------------

/// Direct reproduction: `usize::is_multiple_of(0)` is vacuously true for `data.len() ==
/// 0`, so the old length guard did not catch `/Columns 0`. `data.chunks(0)` then panics.
/// The new `checked_pixel_bytes_per_row` rejects `Columns == 0` before any chunking.
///
/// Fails if: `decode_predictor` panics, or returns `Ok`.
#[test]
fn tiff_predictor_zero_columns_on_empty_data_returns_err_not_panic() {
    let params = DecodeParams {
        predictor: 2,
        columns: 0,
        colors: 1,
        bits_per_component: 8,
    };
    let result = decode_predictor(&[], &params);
    assert!(result.is_err(), "Columns=0 must be rejected, got {result:?}");
}

/// The PNG-predictor family (10-15) computes its row size the same way and must be
/// guarded identically — `checked_bytes_per_row` covers both `decode_tiff_predictor` and
/// `decode_png_predictor` from the single validation point in `DecodeParams`.
///
/// Fails if: `decode_predictor` panics, or returns `Ok`.
#[test]
fn png_predictor_zero_columns_on_empty_data_returns_err_not_panic() {
    let params = DecodeParams {
        predictor: 12,
        columns: 0,
        colors: 1,
        bits_per_component: 8,
    };
    let result = decode_predictor(&[], &params);
    assert!(
        result.is_err(),
        "Columns=0 must be rejected for PNG predictors too, got {result:?}"
    );
}

/// Same defect through the full `Object` path, with an empty decoded stream (`/Columns
/// 0` combined with zero-length ASCIIHexDecode output) — the exact shape from the task's
/// reproduction.
///
/// Fails if: the call panics, or returns `Ok`.
#[test]
fn object_stream_with_zero_columns_on_empty_data_returns_err_not_panic() {
    let stream = stream_with_filter_and_decode_parms(b"", "ASCIIHexDecode", Some(decode_parms_dict(2, 0, 1, 8)));
    let result = stream.decode_stream_data();
    assert!(result.is_err(), "Columns=0 on empty data must error, got {result:?}");
}

// ---------------------------------------------------------------------------
// Defect 1: a declared -1 becomes usize::MAX and overflows the row-size multiply.
// ---------------------------------------------------------------------------

/// `Object::as_integer().unwrap_or(n) as usize` turns a declared `/Columns -1` into
/// `usize::MAX`. `checked_pixel_bytes_per_row` must reject the resulting overflowing
/// multiply rather than wrap to a small, wrong row size.
///
/// Fails if: `decode_predictor` panics, wraps silently (returns `Ok` with wrong-length
/// output), or hangs attempting to allocate.
#[test]
fn negative_columns_becomes_usize_max_and_overflow_is_rejected() {
    let params = DecodeParams {
        predictor: 2,
        columns: -1i64 as usize,
        colors: 1,
        bits_per_component: 8,
    };
    let result = decode_predictor(&[0u8], &params);
    assert!(
        result.is_err(),
        "usize::MAX Columns must overflow-reject, got {result:?}"
    );
}

/// Same overflow, through the full `Object` path with a literal `/Columns -1` in the
/// DecodeParms dictionary — proving `object.rs::extract_decode_params`'s cast feeds
/// straight into the guard with no intermediate clamping that would mask the defect.
///
/// Fails if: the call panics, hangs, or returns `Ok`.
#[test]
fn object_stream_with_negative_columns_returns_err_not_panic() {
    let stream = stream_with_filter_and_decode_parms(b"00", "ASCIIHexDecode", Some(decode_parms_dict(2, -1, 1, 8)));
    let result = stream.decode_stream_data();
    assert!(result.is_err(), "negative /Columns must be rejected, got {result:?}");
}

// ---------------------------------------------------------------------------
// Defect 2: chained uncapped filters must be rejected by the ratio/size guard,
// not by actually allocating the fully expanded output.
// ---------------------------------------------------------------------------

/// `/Filter [/RunLengthDecode /RunLengthDecode /RunLengthDecode /RunLengthDecode]` on a
/// carefully small 2-byte input: the byte pair `0x81 0x81` is a RunLengthDecode "repeat"
/// instruction (length 129 -> repeat count 257-129=128) whose repeated byte is itself
/// `0x81`, so each application re-triggers the same instruction on the next stage's
/// output. This compounds fast (2 -> 128 -> 8192 bytes) and must be rejected by the
/// ratio/size guard after the second filter — well before filters 3 and 4 ever run, and
/// while total memory use is a few KB, not gigabytes.
///
/// Fails if: this call succeeds (`Ok`), fully materializes 64^4-scale output, or the
/// error is unrelated to the decompression-bomb guard (e.g. an unsupported-filter error,
/// which would mean the guard was bypassed for the wrong reason).
#[test]
fn chained_runlength_decode_bomb_rejected_by_ratio_guard_not_by_allocating() {
    let data = vec![0x81u8, 0x81u8];
    let filters = vec![
        "RunLengthDecode".to_string(),
        "RunLengthDecode".to_string(),
        "RunLengthDecode".to_string(),
        "RunLengthDecode".to_string(),
    ];

    let result = decode_stream_with_params(&data, &filters, None);

    let err = result.expect_err("chained RunLengthDecode expansion must be rejected, not allocated");
    let message = err.to_string();
    assert!(
        message.contains("bomb") || message.contains("ratio") || message.contains("limit"),
        "expected a decompression-bomb-guard error, got: {message}"
    );
}

/// Same chained-filter bomb, reached through `Object::decode_stream_data` with a
/// `/Filter` array, matching the exact shape a malicious PDF stream dictionary would use.
///
/// Fails if: this call succeeds, or takes more than a trivial amount of memory/time.
#[test]
fn object_stream_chained_runlength_decode_bomb_is_rejected() {
    let mut dict = HashMap::new();
    dict.insert(
        "Filter".to_string(),
        Object::Array(vec![
            Object::Name("RunLengthDecode".to_string()),
            Object::Name("RunLengthDecode".to_string()),
            Object::Name("RunLengthDecode".to_string()),
            Object::Name("RunLengthDecode".to_string()),
        ]),
    );
    let stream = Object::Stream {
        dict,
        data: Bytes::from_static(&[0x81u8, 0x81u8]),
    };

    let result = stream.decode_stream_data();
    assert!(
        result.is_err(),
        "chained RunLengthDecode bomb must be rejected via Object too, got {result:?}"
    );
}

// ---------------------------------------------------------------------------
// Positive controls: the guards above must not corrupt or reject ordinary streams.
// A cap set too tight would silently break every real PDF using that filter/predictor.
// ---------------------------------------------------------------------------

/// An ordinary PNG Up-predictor (`/Predictor 12`) stream must still decode to exactly the
/// expected bytes. This is the same fixture shape as `predictor.rs`'s own
/// `test_png_up_predictor` unit test, re-verified here at the `decode_stream_with_params`
/// entry point production code actually calls.
///
/// Fails if: the decoded output differs from the expected bytes at all, or the call now
/// errors where it previously succeeded.
#[test]
fn ordinary_png_up_predictor_still_decodes_exactly() {
    let params = DecodeParams {
        predictor: 12,
        columns: 5,
        colors: 1,
        bits_per_component: 8,
    };
    // Row tag 2 (Up) + 5 bytes, twice.
    let encoded = vec![2, 10, 20, 30, 40, 50, 2, 5, 5, 5, 5, 5];

    let decoded = decode_stream_with_params(&encoded, &[], Some(&params)).expect("ordinary PNG predictor must decode");

    assert_eq!(decoded, vec![10, 20, 30, 40, 50, 15, 25, 35, 45, 55]);
}

/// An ordinary single FlateDecode stream must still decode unchanged under the new
/// default ratio/size guard.
///
/// Fails if: the decoded bytes differ from the original at all, or the call now errors.
#[test]
fn ordinary_single_flate_decode_stream_still_decodes_unchanged() {
    let original = b"The quick brown fox jumps over the lazy dog.".repeat(20);
    let mut encoder = ZlibEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(&original).unwrap();
    let compressed = encoder.finish().unwrap();

    let filters = vec!["FlateDecode".to_string()];
    let decoded =
        decode_stream_with_params(&compressed, &filters, None).expect("ordinary FlateDecode stream must decode");

    assert_eq!(decoded, original);
}

/// The same ordinary FlateDecode stream through the full `Object` path.
///
/// Fails if: the decoded bytes differ from the original, or the call errors.
#[test]
fn object_stream_ordinary_flate_decode_still_decodes_unchanged() {
    let original = b"Ordinary, unremarkable stream content.";
    let mut encoder = ZlibEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(original).unwrap();
    let compressed = encoder.finish().unwrap();

    let mut dict = HashMap::new();
    dict.insert("Filter".to_string(), Object::Name("FlateDecode".to_string()));
    let stream = Object::Stream {
        dict,
        data: Bytes::from(compressed),
    };

    let decoded = stream
        .decode_stream_data()
        .expect("ordinary FlateDecode stream must decode via Object path");
    assert_eq!(decoded, original);
}

/// A single, non-chained RunLengthDecode application well under the ratio cap must still
/// decode normally — proving the bomb guard targets compounding expansion, not
/// RunLengthDecode's ordinary (bounded, single-application) use.
///
/// Fails if: the call errors, or the decoded bytes are wrong.
#[test]
fn ordinary_single_runlength_decode_stream_still_decodes_unchanged() {
    let data = vec![4, b'H', b'e', b'l', b'l', b'o'];
    let filters = vec!["RunLengthDecode".to_string()];

    let decoded =
        decode_stream_with_params(&data, &filters, None).expect("ordinary RunLengthDecode stream must decode");

    assert_eq!(decoded, b"Hello");
}
