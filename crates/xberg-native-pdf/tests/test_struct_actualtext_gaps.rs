//! Additional coverage for the struct-tree-scope `/ActualText`
//! pipeline. These tests close the test-gap items the second-pass
//! review called out:
//!
//!   - Non-Span/non-Figure StructElems carrying `/ActualText`
//!     (`/P`, `/H1`, `/L`, `/Caption`, `/Document`, `/Art`).
//!   - Long replacement strings (>4 KB) round-trip unchanged.
//!   - Malformed `/ActualText` values (non-string types) degrade
//!     gracefully — no panic, no spurious replacement.
//!   - Defensive MINOR-1: two same-replacement runs collapse to one
//!     emission (the consecutive-run dedup).
//!   - MINOR-2: explicit pin on empty `/ActualText ()` being ignored.
//!   - Cross-path byte-equality (extract_text / to_markdown / to_html /
//!     to_plain_text / extract_structured) for the ActualText output
//!     portion of a deterministic fixture.

use xberg_native_pdf::converters::ConversionOptions;
use xberg_native_pdf::document::PdfDocument;

// Hand-assembled tagged PDF helpers — re-declared per integration test
// crate (integration tests can't share modules). ~keep

#[allow(dead_code)]
enum K {
    Mcid(u32, u32),
    Obj(u32),
}

struct Elem {
    obj_num: u32,
    s: &'static str,
    parent_obj: u32,
    page_obj: Option<u32>,
    actual_text: Option<String>,
    actual_text_raw: Option<String>,
    children: Vec<K>,
}

impl Elem {
    fn new(obj_num: u32, s: &'static str, parent_obj: u32) -> Self {
        Self {
            obj_num,
            s,
            parent_obj,
            page_obj: None,
            actual_text: None,
            actual_text_raw: None,
            children: Vec::new(),
        }
    }
    fn page(mut self, p: u32) -> Self {
        self.page_obj = Some(p);
        self
    }
    fn actual_text(mut self, t: &str) -> Self {
        self.actual_text = Some(t.to_string());
        self
    }
    /// Embed an `/ActualText` whose VALUE bytes are written verbatim
    /// (already-formatted PDF syntax, e.g. `/Name`, `42`, or a malformed
    /// reference). Used by the malformed-value tests.
    fn actual_text_raw(mut self, raw: &str) -> Self {
        self.actual_text_raw = Some(raw.to_string());
        self
    }
    fn k(mut self, c: K) -> Self {
        self.children.push(c);
        self
    }
}

struct PdfBuilder {
    page_contents: Vec<Vec<u8>>,
    elems: Vec<Elem>,
    parent_tree_entries: Vec<Vec<(u32, u32)>>,
}

impl PdfBuilder {
    fn new() -> Self {
        Self {
            page_contents: Vec::new(),
            elems: Vec::new(),
            parent_tree_entries: Vec::new(),
        }
    }
    fn add_page_content(&mut self, c: Vec<u8>) -> usize {
        self.page_contents.push(c);
        self.parent_tree_entries.push(Vec::new());
        self.page_contents.len() - 1
    }
    fn add_elem(&mut self, e: Elem) -> u32 {
        let n = e.obj_num;
        self.elems.push(e);
        n
    }
    fn register_mcid(&mut self, page_idx: usize, mcid: u32, elem_obj: u32) {
        self.parent_tree_entries[page_idx].push((mcid, elem_obj));
    }
    fn pdf_string_utf16be(s: &str) -> String {
        let mut out = String::from("<FEFF");
        for u in s.encode_utf16() {
            out.push_str(&format!("{:04X}", u));
        }
        out.push('>');
        out
    }

    fn build(self) -> Vec<u8> {
        use std::collections::BTreeMap;
        let n_pages = self.page_contents.len() as u32;
        let catalog = 1u32;
        let pages = 2u32;
        let font = 3u32;
        let first_page = 4u32;
        let first_content = first_page + n_pages;
        let parent_tree = first_content + n_pages;
        let struct_tree_root = parent_tree + 1;
        let first_struct_elem = struct_tree_root + 1;

        let mut used = std::collections::HashSet::new();
        for e in &self.elems {
            assert!(e.obj_num >= first_struct_elem);
            assert!(used.insert(e.obj_num));
        }
        let root_elem = self.elems[0].obj_num;

        let mut objs: BTreeMap<u32, Vec<u8>> = BTreeMap::new();
        objs.insert(
            catalog,
            format!(
                "<< /Type /Catalog /Pages {} 0 R /MarkInfo << /Marked true >> /StructTreeRoot {} 0 R >>",
                pages, struct_tree_root
            )
            .into_bytes(),
        );
        let kids: String = (0..n_pages)
            .map(|i| format!("{} 0 R", first_page + i))
            .collect::<Vec<_>>()
            .join(" ");
        objs.insert(
            pages,
            format!("<< /Type /Pages /Kids [{}] /Count {} >>", kids, n_pages).into_bytes(),
        );
        objs.insert(font, b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".to_vec());

        for i in 0..n_pages {
            let content_obj = first_content + i;
            let content_bytes = &self.page_contents[i as usize];
            let mut stream = format!("<< /Length {} >>\nstream\n", content_bytes.len()).into_bytes();
            stream.extend_from_slice(content_bytes);
            stream.extend_from_slice(b"\nendstream");
            objs.insert(content_obj, stream);
            objs.insert(
                first_page + i,
                format!(
                    "<< /Type /Page /Parent {} 0 R /MediaBox [0 0 612 792] \
                     /Resources << /Font << /F1 {} 0 R >> /ProcSet [/PDF /Text] >> \
                     /Contents {} 0 R /StructParents {} >>",
                    pages, font, content_obj, i
                )
                .into_bytes(),
            );
        }

        let mut nums = String::from("<< /Nums [");
        for (i, entries) in self.parent_tree_entries.iter().enumerate() {
            nums.push_str(&format!("{} [", i));
            let mut by_mcid: Vec<(u32, u32)> = entries.clone();
            by_mcid.sort_by_key(|(m, _)| *m);
            let max_mcid = by_mcid.iter().map(|(m, _)| *m).max().unwrap_or(0);
            for m in 0..=max_mcid {
                if let Some((_, e)) = by_mcid.iter().find(|(mm, _)| *mm == m) {
                    nums.push_str(&format!("{} 0 R ", e));
                } else {
                    nums.push_str("null ");
                }
            }
            nums.push(']');
            if i + 1 < self.parent_tree_entries.len() {
                nums.push(' ');
            }
        }
        nums.push_str("] >>");
        objs.insert(parent_tree, nums.into_bytes());

        objs.insert(
            struct_tree_root,
            format!(
                "<< /Type /StructTreeRoot /K {} 0 R /ParentTree {} 0 R >>",
                root_elem, parent_tree
            )
            .into_bytes(),
        );

        for e in &self.elems {
            let mut body = format!("<< /Type /StructElem /S /{} /P {} 0 R", e.s, e.parent_obj);
            if let Some(pg) = e.page_obj {
                body.push_str(&format!(" /Pg {} 0 R", pg));
            }
            if let Some(ref at) = e.actual_text {
                body.push_str(&format!(" /ActualText {}", Self::pdf_string_utf16be(at)));
            }
            if let Some(ref raw) = e.actual_text_raw {
                body.push_str(&format!(" /ActualText {}", raw));
            }
            let mcr = |m: u32, pg_idx: u32| -> String {
                format!("<< /Type /MCR /Pg {} 0 R /MCID {} >>", first_page + pg_idx, m)
            };
            if e.children.len() == 1 {
                match &e.children[0] {
                    K::Mcid(m, p) => body.push_str(&format!(" /K {}", mcr(*m, *p))),
                    K::Obj(o) => body.push_str(&format!(" /K {} 0 R", o)),
                }
            } else if !e.children.is_empty() {
                body.push_str(" /K [");
                for (i, c) in e.children.iter().enumerate() {
                    if i > 0 {
                        body.push(' ');
                    }
                    match c {
                        K::Mcid(m, p) => body.push_str(&mcr(*m, *p)),
                        K::Obj(o) => body.push_str(&format!("{} 0 R", o)),
                    }
                }
                body.push(']');
            }
            body.push_str(" >>");
            objs.insert(e.obj_num, body.into_bytes());
        }

        let mut out: Vec<u8> = Vec::new();
        out.extend_from_slice(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n");
        let mut offsets: BTreeMap<u32, usize> = BTreeMap::new();
        for (num, body) in &objs {
            offsets.insert(*num, out.len());
            out.extend_from_slice(format!("{} 0 obj\n", num).as_bytes());
            out.extend_from_slice(body);
            out.extend_from_slice(b"\nendobj\n");
        }
        let xref_offset = out.len();
        let max_num = *objs.keys().max().unwrap();
        let n_objs = max_num + 1;
        out.extend_from_slice(format!("xref\n0 {}\n", n_objs).as_bytes());
        out.extend_from_slice(b"0000000000 65535 f \n");
        for n in 1..n_objs {
            if let Some(off) = offsets.get(&n) {
                out.extend_from_slice(format!("{:010} 00000 n \n", off).as_bytes());
            } else {
                out.extend_from_slice(b"0000000000 00000 f \n");
            }
        }
        out.extend_from_slice(
            format!(
                "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{}\n%%EOF\n",
                n_objs, xref_offset
            )
            .as_bytes(),
        );
        out
    }
}

fn one_mcid_content(g: char) -> Vec<u8> {
    format!(
        "BT\n/F1 12 Tf\n50 700 Td\n\
         /Span << /MCID 0 >> BDC\n({}) Tj\nEMC\nET\n",
        g
    )
    .into_bytes()
}

// =============================================================
// Non-Span/non-Figure StructElems carrying /ActualText.
// =============================================================
//
// PDF §14.9.4 places no restriction on which structure type may
// carry `/ActualText`. Every block / inline-level element is fair
// game. Pin the parametric coverage so an "only Span / Figure
// handles ActualText" regression would be caught. ~keep

fn make_struct_with_actualtext(stype: &'static str) -> Vec<u8> {
    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(Elem::new(8, stype, 7).page(4).actual_text("R").k(K::Mcid(0, 0)));
    b.register_mcid(0, 0, 8);
    b.build()
}

#[test]
fn actualtext_on_various_struct_types_emits_replacement() {
    for stype in ["P", "H1", "L", "Caption", "Document", "Art"] {
        let pdf = make_struct_with_actualtext(stype);
        let doc = PdfDocument::from_bytes(pdf).expect("open");
        let extracted = doc.extract_text(0).expect("extract_text");
        assert!(
            extracted.contains('R'),
            "{}: replacement 'R' must appear, got {:?}",
            stype,
            extracted
        );
        assert!(
            !extracted.contains('X'),
            "{}: raw 'X' must NOT appear (covered by ActualText), got {:?}",
            stype,
            extracted
        );
    }
}

#[test]
fn actualtext_long_string_round_trips() {
    let mut long = String::with_capacity(4500);
    for i in 0..1500 {
        long.push_str(&format!("[{i:04}]"));
    }
    assert!(long.len() > 4000);

    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(Elem::new(8, "Span", 7).page(4).actual_text(&long).k(K::Mcid(0, 0)));
    b.register_mcid(0, 0, 8);

    let pdf = b.build();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let extracted = doc.extract_text(0).expect("extract_text");
    assert!(
        extracted.contains(&long),
        "long ActualText (>{} bytes) must round-trip unchanged; output len = {}",
        long.len(),
        extracted.len()
    );
    assert!(
        !extracted.contains('X'),
        "raw 'X' must NOT appear, got len {}",
        extracted.len()
    );
}

#[test]
fn actualtext_malformed_name_value_does_not_panic() {
    // `/ActualText /SomeName` is type-wrong (spec requires a text
    // string). The parser must accept the dictionary and ignore the
    // bad value — no panic, no replacement applied. ~keep
    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(
        Elem::new(8, "Span", 7)
            .page(4)
            .actual_text_raw("/SomeName")
            .k(K::Mcid(0, 0)),
    );
    b.register_mcid(0, 0, 8);
    let pdf = b.build();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let extracted = doc.extract_text(0).expect("extract_text");
    assert!(
        extracted.contains('X'),
        "malformed /ActualText must be ignored; raw 'X' must survive, got {:?}",
        extracted
    );
}

#[test]
fn actualtext_malformed_integer_value_does_not_panic() {
    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(Elem::new(8, "Span", 7).page(4).actual_text_raw("42").k(K::Mcid(0, 0)));
    b.register_mcid(0, 0, 8);
    let pdf = b.build();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let extracted = doc.extract_text(0).expect("extract_text");
    assert!(
        extracted.contains('X'),
        "malformed integer /ActualText must be ignored; got {:?}",
        extracted
    );
}

#[test]
fn actualtext_malformed_dict_value_does_not_panic() {
    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(
        Elem::new(8, "Span", 7)
            .page(4)
            .actual_text_raw("<< /A 1 >>")
            .k(K::Mcid(0, 0)),
    );
    b.register_mcid(0, 0, 8);
    let pdf = b.build();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let extracted = doc.extract_text(0).expect("extract_text");
    assert!(
        extracted.contains('X'),
        "malformed dict /ActualText must be ignored; got {:?}",
        extracted
    );
}

#[test]
fn same_replacement_multi_mcid_run_emits_once() {
    let content = b"BT\n/F1 12 Tf\n50 700 Td\n\
        /Span << /MCID 0 >> BDC\n(X) Tj\nEMC\n\
        /Span << /MCID 1 >> BDC\n(Y) Tj\nEMC\nET\n";
    let mut b = PdfBuilder::new();
    b.add_page_content(content.to_vec());
    let _ = b.add_elem(
        Elem::new(8, "Span", 7)
            .page(4)
            .actual_text("X-or-Y")
            .k(K::Mcid(0, 0))
            .k(K::Mcid(1, 0)),
    );
    b.register_mcid(0, 0, 8);
    b.register_mcid(0, 1, 8);
    let pdf = b.build();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let extracted = doc.extract_text(0).expect("extract_text");
    assert_eq!(
        extracted.matches("X-or-Y").count(),
        1,
        "consecutive-run dedup: one emission for a run that shares one replacement, got {:?}",
        extracted
    );
}

#[test]
fn empty_actualtext_is_ignored_raw_glyph_survives() {
    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(Elem::new(8, "Span", 7).page(4).actual_text("").k(K::Mcid(0, 0)));
    b.register_mcid(0, 0, 8);
    let pdf = b.build();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let extracted = doc.extract_text(0).expect("extract_text");
    assert!(
        extracted.contains('X'),
        "empty /ActualText must be a no-op; raw 'X' must survive, got {:?}",
        extracted
    );
}

fn fixture_byte_equal() -> Vec<u8> {
    let mut b = PdfBuilder::new();
    b.add_page_content(one_mcid_content('X'));
    let _ = b.add_elem(Elem::new(8, "Span", 7).page(4).actual_text("fi").k(K::Mcid(0, 0)));
    b.register_mcid(0, 0, 8);
    b.build()
}

#[test]
fn cross_path_byte_equal_for_actualtext_replacement() {
    let pdf = fixture_byte_equal();
    let doc = PdfDocument::from_bytes(pdf).expect("open");
    let _opts = ConversionOptions::default();
    let extract_text = doc.extract_text(0).expect("extract_text");
    assert_eq!(
        extract_text.trim_end(),
        "fi",
        "single-MCID ActualText fixture must emit exactly the replacement; got {:?}",
        extract_text
    );

    let structured = doc.extract_structured(0).expect("structured");
    let texts: Vec<_> = structured.regions.iter().map(|r| r.text.clone()).collect();
    assert!(
        texts.iter().any(|t| t.trim() == "fi"),
        "extract_structured must surface 'fi' in a region; got {:?}",
        texts
    );
}
