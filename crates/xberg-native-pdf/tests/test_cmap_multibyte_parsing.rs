//! CMap multi-byte parsing correctness tests (§9.7.5 / §9.10.3).
//!
//! Focuses on three specific parsing correctness requirements:
//!
//! 1. **Array-form `beginbfrange`**: `<src_start> <src_end> [<dst1> <dst2> ...]`
//!    Each array entry maps `src_start + i → dst_i`.  Used for ligatures and
//!    irregular CJK sub-ranges.
//!
//! 2. **Multi-byte hex strings**: `<4E2D>` is a single 2-byte code 0x4E2D, not
//!    bytes 0x4E and 0x2D read separately.
//!
//! 3. **`begincodespacerange` drives byte-width**: When the codespace declares
//!    2-byte codes (`<0000> <FFFF>`), `LazyCMap::code_width()` must return 2 so
//!    that the text extractor switches from 1-byte to 2-byte character reading.

use xberg_native_pdf::fonts::cmap::{LazyCMap, parse_tounicode_cmap};

#[test]
fn test_bfrange_array_form_basic() {
    let data = b"beginbfrange\n<0041> <0043> [<FF21> <FF22> <FF23>]\nendbfrange";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(
        cmap.get(&0x0041).as_deref(),
        Some("\u{FF21}"),
        "0x41 → Fullwidth A (U+FF21)"
    );
    assert_eq!(
        cmap.get(&0x0042).as_deref(),
        Some("\u{FF22}"),
        "0x42 → Fullwidth B (U+FF22)"
    );
    assert_eq!(
        cmap.get(&0x0043).as_deref(),
        Some("\u{FF23}"),
        "0x43 → Fullwidth C (U+FF23)"
    );
}

#[test]
fn test_bfrange_array_form_ligatures() {
    // PDF spec §9.10.3 example: <005F> <0061> [<00660066> <00660069> <00660066006C>]
    // Codes 0x5F→"ff", 0x60→"fi", 0x61→"ffl" ~keep
    let data = b"beginbfrange\n<005F> <0061> [<00660066> <00660069> <00660066006C>]\nendbfrange";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(cmap.get(&0x5F).as_deref(), Some("ff"), "code 0x5F → \"ff\"");
    assert_eq!(cmap.get(&0x60).as_deref(), Some("fi"), "code 0x60 → \"fi\"");
    assert_eq!(cmap.get(&0x61).as_deref(), Some("ffl"), "code 0x61 → \"ffl\"");
}

#[test]
fn test_bfrange_array_form_cjk() {
    // 2-byte source codes with 2-byte destinations — typical CJK ToUnicode CMap snippet
    // beginbfrange <4E00> <4E02> [<4E00> <4E01> <4E02>] ~keep
    let data = b"beginbfrange\n<4E00> <4E02> [<4E00> <4E01> <4E02>]\nendbfrange";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(cmap.get(&0x4E00).as_deref(), Some("\u{4E00}"), "一 identity");
    assert_eq!(cmap.get(&0x4E01).as_deref(), Some("\u{4E01}"), "丁 identity");
    assert_eq!(cmap.get(&0x4E02).as_deref(), Some("\u{4E02}"), "丂 identity");
}

#[test]
fn test_bfrange_linear_form_still_works() {
    let data = b"beginbfrange\n<0041> <0045> <0061>\nendbfrange";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(cmap.get(&0x41).as_deref(), Some("a"));
    assert_eq!(cmap.get(&0x42).as_deref(), Some("b"));
    assert_eq!(cmap.get(&0x43).as_deref(), Some("c"));
    assert_eq!(cmap.get(&0x44).as_deref(), Some("d"));
    assert_eq!(cmap.get(&0x45).as_deref(), Some("e"));
}

#[test]
fn test_bfchar_two_byte_src_code() {
    let data = b"beginbfchar\n<4E2D> <4E2D>\nendbfchar";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(
        cmap.get(&0x4E2D).as_deref(),
        Some("\u{4E2D}"),
        "code 0x4E2D → U+4E2D (中)"
    );
    assert!(
        cmap.get(&0x4E).is_none() || cmap.get(&0x4E).as_deref() != Some("\u{4E2D}"),
        "byte 0x4E must not produce 中"
    );
}

#[test]
fn test_bfchar_two_byte_src_hiragana() {
    let data = b"beginbfchar\n<3042> <3042>\nendbfchar";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(
        cmap.get(&0x3042).as_deref(),
        Some("\u{3042}"),
        "code 0x3042 → U+3042 (あ)"
    );
}

#[test]
fn test_bfchar_two_byte_multiple_cjk() {
    let data = b"beginbfchar\n<4E2D> <4E2D>\n<6587> <6587>\n<5B66> <5B66>\nendbfchar";
    let cmap = parse_tounicode_cmap(data).unwrap();

    assert_eq!(cmap.get(&0x4E2D).as_deref(), Some("\u{4E2D}"), "中 (0x4E2D)");
    assert_eq!(cmap.get(&0x6587).as_deref(), Some("\u{6587}"), "文 (0x6587)");
    assert_eq!(cmap.get(&0x5B66).as_deref(), Some("\u{5B66}"), "学 (0x5B66)");
}

#[test]
fn test_codespacerange_two_byte_sets_code_width() {
    let data = b"/CIDInit /ProcSet findresource begin\nbegincmap\n\
        1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n\
        1 beginbfchar\n<3042> <3042>\nendbfchar\nendcmap";

    let cmap = parse_tounicode_cmap(data).unwrap();
    assert_eq!(cmap.code_width, 2, "2-byte codespace must set code_width = 2");

    assert_eq!(cmap.get(&0x3042).as_deref(), Some("\u{3042}"), "あ lookup");
}

#[test]
fn test_codespacerange_one_byte_keeps_default() {
    let data = b"1 begincodespacerange\n<00> <FF>\nendcodespacerange\n\
        1 beginbfchar\n<41> <41>\nendbfchar";

    let cmap = parse_tounicode_cmap(data).unwrap();
    assert_eq!(cmap.code_width, 1, "1-byte codespace keeps code_width = 1");
    assert_eq!(cmap.get(&0x41).as_deref(), Some("A"), "A lookup");
}

#[test]
fn test_lazycmap_code_width_two_byte() {
    let cmap_data = b"1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n\
        2 beginbfchar\n<4E2D> <4E2D>\n<6587> <6587>\nendbfchar"
        .to_vec();

    let lazy = LazyCMap::new(cmap_data);
    assert_eq!(lazy.code_width(), 2, "LazyCMap::code_width() should return 2");
}

#[test]
fn test_lazycmap_code_width_one_byte() {
    let cmap_data = b"1 begincodespacerange\n<00> <FF>\nendcodespacerange\n\
        1 beginbfchar\n<41> <41>\nendbfchar"
        .to_vec();

    let lazy = LazyCMap::new(cmap_data);
    assert_eq!(lazy.code_width(), 1, "LazyCMap::code_width() should return 1");
}

#[test]
fn test_lazycmap_code_width_default_when_no_codespace() {
    let cmap_data = b"1 beginbfchar\n<41> <41>\nendbfchar".to_vec();

    let lazy = LazyCMap::new(cmap_data);
    assert_eq!(lazy.code_width(), 1, "Missing codespace defaults code_width = 1");
}

#[test]
fn test_full_cjk_cmap_roundtrip() {
    // Simulates a realistic ToUnicode CMap for a CJK composite font ~keep
    let cmap_data = br#"
/CIDInit /ProcSet findresource begin
12 dict begin
begincmap
/CIDSystemInfo
<< /Registry (Adobe)
/Ordering (UCS)
/Supplement 0
>> def
/CMapName /Adobe-Identity-UCS def
/CMapType 2 def
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
3 beginbfchar
<4E2D> <4E2D>
<6587> <6587>
<3042> <3042>
endbfchar
1 beginbfrange
<4E00> <4E05> <4E00>
endbfrange
endcmap
CMapName currentdict /CMap defineresource pop
end
end
"#;

    let lazy = LazyCMap::new(cmap_data.to_vec());

    assert_eq!(lazy.code_width(), 2, "full CJK CMap code_width = 2");

    let cmap = lazy.get().expect("CMap must parse");

    assert_eq!(cmap.get(&0x4E2D).as_deref(), Some("\u{4E2D}"), "中");
    assert_eq!(cmap.get(&0x6587).as_deref(), Some("\u{6587}"), "文");
    assert_eq!(cmap.get(&0x3042).as_deref(), Some("\u{3042}"), "あ");

    assert_eq!(cmap.get(&0x4E00).as_deref(), Some("\u{4E00}"), "一");
    assert_eq!(cmap.get(&0x4E03).as_deref(), Some("\u{4E03}"), "七");
    assert_eq!(cmap.get(&0x4E05).as_deref(), Some("\u{4E05}"), "丅");
}
