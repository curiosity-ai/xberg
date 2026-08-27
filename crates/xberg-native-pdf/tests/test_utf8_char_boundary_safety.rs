//! Regression tests for UTF-8 character boundary safety in string slicing.
//!
//! Validates that the byte-offset string slicing patterns used in log/debug
//! macros do not panic on multi-byte UTF-8 characters. The original bug
//! affected four sites in text.rs and xref.rs where `&s[..n]` or
//! `&s[n..]` was used with byte offsets that could land inside multi-byte
//! UTF-8 sequences (CJK, emoji, dingbats, U+FFFD replacement chars).

/// Truncate from the front at a char boundary (same logic as crate::utils::safe_prefix).
fn safe_prefix(s: &str, max_bytes: usize) -> &str {
    if s.len() <= max_bytes {
        return s;
    }
    let mut end = max_bytes;
    while end > 0 && !s.is_char_boundary(end) {
        end -= 1;
    }
    &s[..end]
}

/// Truncate from the back at a char boundary (same logic as crate::utils::safe_suffix).
fn safe_suffix(s: &str, max_bytes: usize) -> &str {
    if s.len() <= max_bytes {
        return s;
    }
    let start = s.len() - max_bytes;
    let mut safe_start = start;
    while safe_start < s.len() && !s.is_char_boundary(safe_start) {
        safe_start += 1;
    }
    &s[safe_start..]
}

#[test]
fn test_old_prefix_pattern_panics_on_multibyte() {
    // Reproduces the exact pattern from text.rs:2963
    //   &span.text[..span.text.len().min(10)] ~keep
    let text = "✚✳★✵";
    let result = std::panic::catch_unwind(|| &text[..text.len().min(10)]);
    assert!(
        result.is_err(),
        "old byte-offset prefix slice should panic on multi-byte UTF-8"
    );
}

#[test]
fn test_old_suffix_pattern_panics_on_multibyte() {
    // Reproduces the exact pattern from text.rs:2961
    //   &current.text[current.text.len().saturating_sub(10)..] ~keep
    let text = "AB✚✳★✵";
    let result = std::panic::catch_unwind(|| &text[text.len().saturating_sub(10)..]);
    assert!(
        result.is_err(),
        "old byte-offset suffix slice should panic on multi-byte UTF-8"
    );
}

// Regression test: the fixed patterns do not panic ~keep

/// Site 1a — text.rs:2963: `&span.text[..span.text.len().min(10)]`
#[test]
fn test_fixed_prefix_slice_min10() {
    let text = "✚✳★✵";
    let result = safe_prefix(text, 10);
    assert_eq!(result, "✚✳★");
}

/// Site 1b — text.rs:2961: `&current.text[current.text.len().saturating_sub(10)..]`
#[test]
fn test_fixed_suffix_saturating_sub10() {
    let text = "AB✚✳★✵";
    let result = safe_suffix(text, 10);
    assert_eq!(result, "✳★✵");
}

/// Site 2 — text.rs:5697: `&span.text[..span.text.len().min(20)]`
#[test]
fn test_fixed_prefix_slice_min20_cjk() {
    let text = "你好世界测试代码";
    assert_eq!(text.len(), 24);
    let result = safe_prefix(text, 20);
    assert_eq!(result, "你好世界测试");
    assert_eq!(result.len(), 18);
}

/// Site 3 — xref.rs:391: `&peek_str[..peek_str.len().min(15)]`
/// peek_str comes from String::from_utf8_lossy which can produce U+FFFD (3 bytes).
#[test]
fn test_fixed_prefix_slice_min15_replacement_chars() {
    let text = "xref \u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}";
    assert_eq!(text.len(), 17);
    let result = safe_prefix(text, 15);
    assert_eq!(result, "xref \u{FFFD}\u{FFFD}\u{FFFD}");
    assert_eq!(result.len(), 14);
}

/// Site 4 — xref.rs:421: `&trimmed[..trimmed.len().min(20)]`
#[test]
fn test_fixed_prefix_slice_min20_replacement_chars() {
    let text = "obj \u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}";
    assert_eq!(text.len(), 22);
    let result = safe_prefix(text, 20);
    assert_eq!(result, "obj \u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}\u{FFFD}");
    assert_eq!(result.len(), 19);
}

#[test]
fn test_safe_prefix_empty_string() {
    assert_eq!(safe_prefix("", 10), "");
}

#[test]
fn test_safe_prefix_zero_max() {
    assert_eq!(safe_prefix("hello", 0), "");
}

#[test]
fn test_safe_suffix_empty_string() {
    assert_eq!(safe_suffix("", 10), "");
}

#[test]
fn test_safe_suffix_zero_max() {
    assert_eq!(safe_suffix("hello", 0), "");
}

#[test]
fn test_safe_prefix_4byte_emoji() {
    let text = "😀😁😂";
    assert_eq!(safe_prefix(text, 5), "😀");
}

#[test]
fn test_safe_suffix_4byte_emoji() {
    let text = "😀😁😂";
    assert_eq!(safe_suffix(text, 5), "😂");
}
