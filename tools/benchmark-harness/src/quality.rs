//! Quality scoring module for benchmark results.
//!
//! Computes F1-based quality metrics by comparing extracted text against ground truth.
//! Uses token-level (bag-of-words) precision and recall.
//!
//! # Scoring weights
//!
//! Text-only scoring uses a **0.6 / 0.4 text / numeric split**:
//!
//! ```text
//! quality_score = 0.6 * f1_text + 0.4 * f1_numeric
//! ```
//!
//! Numeric tokens receive disproportionate weight (40% despite typically being
//! a small fraction of the token count) because financial documents, scientific
//! papers, and tabular data depend heavily on number accuracy. A single wrong
//! digit can invalidate an entire table row or equation.
//!
//! When markdown ground truth is available, **combined scoring** kicks in:
//!
//! ```text
//! quality_score = 0.5 * f1_text + 0.2 * f1_numeric + 0.3 * f1_layout
//! ```
//!
//! The layout component (`f1_layout`) is canonical SF1 from
//! [`structural_sidecar`] and captures structural fidelity across paragraph,
//! heading, list, table, binding-edge, and reading-order dimensions.
//!
//! # Tokenization
//!
//! Tokenization is intentionally simple: NFKC-normalize, lowercase, split on whitespace
//! (with `|` also treated as a separator so markdown table cell padding cannot change the
//! token stream), strip non-alphanumeric characters except periods and commas embedded
//! between alphanumeric characters (preserving decimal numbers like "3.14" and European
//! format "3,14"). CJK runs use character bigrams because Chinese, Japanese, and Korean OCR
//! engines commonly insert layout-dependent line breaks mid-word; bigrams never cross a
//! whitespace/line boundary, so reordered lines are still detected as errors. This preserves
//! punctuation that is semantically meaningful while ignoring decorative punctuation.
//!
//! # Reading order (report-only)
//!
//! `f1_score_text` is a bag-of-tokens (multiset) F1: it is order-insensitive by
//! construction, so it cannot detect reading-order failure (a fully scrambled document can
//! still score near-perfect F1 as long as every token survives somewhere). This matters
//! because vertical-Japanese OCR fails precisely by scrambling reading order.
//! [`reading_order_score`] measures order fidelity separately via anchor-based LIS. It is
//! computed and reported on [`QualityMetrics`] but is **intentionally not folded into
//! `quality_score`** pending corpus-wide distribution analysis — see that function's docs.

use crate::types::{OutputFormat, QualityMetrics};
use regex::Regex;
use std::collections::HashMap;
use std::sync::LazyLock;
use unicode_normalization::UnicodeNormalization;

// The structural-sidecar file lives at `src/structural_sidecar.rs`; it is attached ~keep
// here (rather than in `lib.rs`) via `#[path]` so the crate root stays untouched.
#[path = "structural_sidecar.rs"]
pub mod structural_sidecar;

/// Regex to strip markdown image syntax `![alt](url)` → `alt`
static MD_IMAGE_RE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"!\[([^\]]*)\]\([^)]*\)").expect("invalid regex"));

/// Regex to strip markdown link syntax `[text](url)` → `text`
static MD_LINK_RE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"\[([^\]]*)\]\([^)]*\)").expect("invalid regex"));

/// Regex matching bare (non-markdown-linked) URLs. Run AFTER [`MD_LINK_RE`]/[`MD_IMAGE_RE`],
/// which already consume the url portion of markdown links, so this only matches URLs that
/// were never wrapped in link syntax. Without this, a bare url survives the alphanumeric
/// filter as junk (`https://example.com` -> `httpsexamplecom`), penalizing precision for a
/// framework that emits bare links relative to one that emits markdown links — both discard
/// the url the same way here. ~keep
static BARE_URL_RE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?:https?://|www\.)\S+").expect("invalid regex"));

/// Strip markdown link and image syntax so URL components don't become tokens.
/// `![alt](url)` → `alt`, `[text](url)` → `text`, and a bare `https://…`/`www.…` URL is
/// dropped entirely for parity with the linked case.
fn strip_markdown_links(text: &str) -> String {
    let text = MD_IMAGE_RE.replace_all(text, "$1");
    let text = MD_LINK_RE.replace_all(&text, "$1");
    BARE_URL_RE.replace_all(&text, "").into_owned()
}

/// Compute quality metrics comparing extracted text against ground truth,
/// optionally including structural quality scoring when markdown GT is available.
///
/// When `output_format` is `Markdown` and `ground_truth_markdown` is `Some`, computes
/// structural F1 from markdown block comparison and adjusts the quality_score formula:
///   quality_score = 0.5 * f1_text + 0.2 * f1_numeric + 0.3 * f1_layout
///
/// When `output_format` is `Plaintext`, returns text-only scoring regardless of
/// markdown ground truth availability:
///   quality_score = 0.6 * f1_text + 0.4 * f1_numeric
///   f1_score_layout = None
///
/// When `output_format` is `Markdown` but `ground_truth_markdown` is `None`, falls back
/// to text-only scoring:
///   quality_score = 0.6 * f1_text + 0.4 * f1_numeric
pub fn compute_quality_with_structure(
    extracted: &str,
    ground_truth: &str,
    ground_truth_markdown: Option<&str>,
    output_format: OutputFormat,
) -> QualityMetrics {
    if output_format == OutputFormat::Plaintext {
        return compute_quality(extracted, ground_truth);
    }

    let mut metrics = compute_quality(extracted, ground_truth);

    if let Some(md_gt) = ground_truth_markdown {
        let structural_f1 = structural_sidecar::score_markdown(extracted, md_gt).sf1;
        metrics.f1_score_layout = Some(structural_f1);
        // Gated on the ground truth alone, not "either side" — see `compute_quality` for the
        // full rationale. Numeric fidelity cannot be measured against a reference that has no
        // numbers, so a stray extracted digit (page number, footnote marker) must not pull in
        // the numeric-weighted formula and collapse the score. ~keep
        metrics.quality_score = if ground_truth_has_numeric_tokens(ground_truth) {
            0.5 * metrics.f1_score_text + 0.2 * metrics.f1_score_numeric + 0.3 * structural_f1
        } else {
            0.625 * metrics.f1_score_text + 0.375 * structural_f1
        };
    }

    metrics.correct = metrics.quality_score >= 0.95;
    metrics
}

/// Compute quality metrics comparing extracted text against ground truth
///
/// Algorithm:
/// 1. Tokenize both texts: lowercase, split on whitespace, strip non-alphanumeric chars except periods and commas
///    - "3.14" is preserved as a single token
///    - "3,14" is preserved as a single token (European decimal format)
/// 2. Build token multisets (bag of words with counts)
/// 3. Compute precision = |intersection| / |extracted tokens|
/// 4. Compute recall = |intersection| / |ground truth tokens|
/// 5. F1 = 2 * precision * recall / (precision + recall)
///    - If both token sets are empty, F1 = 1.0 (vacuously perfect match)
/// 6. Separate F1 for all tokens vs numeric-only tokens
/// 7. quality_score = 0.6 * f1_text + 0.4 * f1_numeric — but ONLY when the ground truth
///    itself contains numeric tokens; see the gating note below.
/// 8. `reading_order_score` is computed separately (anchor-based LIS, see that function's
///    docs) and attached REPORT-ONLY — it does not participate in `quality_score`.
pub fn compute_quality(extracted: &str, ground_truth: &str) -> QualityMetrics {
    let extracted_tokens = tokenize(extracted);
    let truth_tokens = tokenize(ground_truth);

    let f1_score_text = compute_f1(&extracted_tokens, &truth_tokens);

    let extracted_numeric = filter_numeric(&extracted_tokens);
    let truth_numeric = filter_numeric(&truth_tokens);
    let f1_score_numeric = compute_f1(&extracted_numeric, &truth_numeric);

    // Gate the numeric component on the GROUND TRUTH only, not "either side has numerics".
    // Numeric fidelity cannot be measured against a reference that contains no numbers, so an
    // extraction that emits a stray digit (page number, footnote marker, a "Page 3 of 12"
    // footer the ground-truth generator stripped) must not fall through to the 0.6/0.4 split
    // and take a 40% penalty for a header/footer convention mismatch. The extra numeric token
    // still costs precision inside `f1_score_text` via `compute_f1`, so it is not unpunished.
    //
    // This is deliberately ASYMMETRIC: when the ground truth DOES have numerics and the
    // extraction dropped them, `truth_numeric` is non-empty, so this still routes into the
    // 0.6/0.4 split, and `compute_f1` still returns 0.0 for `f1_score_numeric` (one side
    // empty, the other not) — that is a genuine recall failure and must still cost 40%. ~keep
    let quality_score = if truth_numeric.is_empty() {
        f1_score_text
    } else {
        0.6 * f1_score_text + 0.4 * f1_score_numeric
    };

    let (missing_tokens, extra_tokens) = compute_token_diff(&extracted_tokens, &truth_tokens);

    let correct = quality_score >= 0.95;

    // REPORT-ONLY: computed alongside the other scores but never weighted into
    // `quality_score` — see the module-level "Reading order" doc and `reading_order_score`.
    let reading_order_score = reading_order_score_from_tokens(&extracted_tokens, &truth_tokens);

    QualityMetrics {
        f1_score_text,
        f1_score_numeric,
        f1_score_layout: None,
        quality_score,
        missing_tokens,
        extra_tokens,
        correct,
        reading_order_score,
    }
}

/// Minimum anchor count required to report [`reading_order_score`]. Below this, the order
/// signal is too sparse to be meaningful, so the function returns `None` rather than a
/// number computed from a handful of coincidental unique tokens.
const MIN_READING_ORDER_ANCHORS: usize = 8;

/// Reading-order fidelity between `extracted` and `ground_truth`, via anchor-based Longest
/// Increasing Subsequence (LIS).
///
/// REPORT-ONLY: this score is intentionally excluded from `quality_score`. Several other
/// metrics in this module changed today; weighting a brand-new metric into the composite
/// score now would silently re-baseline every previously published number. It is surfaced
/// on [`QualityMetrics::reading_order_score`] for visibility pending corpus-wide
/// distribution analysis.
///
/// # Algorithm
/// 1. Tokenize both sides with [`tokenize`] (reused as-is — NFKC normalization and CJK
///    bigramming already make it order-sensitive at the line-break boundary).
/// 2. An **anchor** is a token that occurs exactly once in `ground_truth` AND exactly once
///    in `extracted` — an unambiguous 1:1 positional correspondence between the two token
///    streams. Repeated tokens are excluded because they have no single position to anchor.
/// 3. Anchors are ordered by their ground-truth index, and `reading_order_score` is the
///    length of the Longest Increasing Subsequence of their extracted-side indices —
///    computed via patience sorting in `O(n log n)`, not `O(n^2)` LIS or edit distance/LCS
///    (both `O(n*m)`), because some extractions in this corpus run to ~140k tokens and a
///    quadratic algorithm is not viable at that scale — divided by the anchor count.
/// 4. Identical order -> `1.0`. Fully reversed order -> near `0.0`. This measures ONLY the
///    order of shared, unambiguous content; content correctness (missing/extra tokens) is
///    already `f1_score_text`'s job and is deliberately not re-litigated here.
///
/// Returns `None` when either side tokenizes to nothing, or when fewer than
/// [`MIN_READING_ORDER_ANCHORS`] anchors are found — insufficient signal to report a
/// number rather than fabricate one.
pub fn reading_order_score(extracted: &str, ground_truth: &str) -> Option<f64> {
    let extracted_tokens = tokenize(extracted);
    let truth_tokens = tokenize(ground_truth);
    reading_order_score_from_tokens(&extracted_tokens, &truth_tokens)
}

fn reading_order_score_from_tokens(extracted_tokens: &[String], truth_tokens: &[String]) -> Option<f64> {
    if extracted_tokens.is_empty() || truth_tokens.is_empty() {
        return None;
    }

    let anchor_extracted_indices = anchor_extracted_indices_by_gt_order(extracted_tokens, truth_tokens);
    if anchor_extracted_indices.len() < MIN_READING_ORDER_ANCHORS {
        return None;
    }

    let lis_len = longest_increasing_subsequence_len(&anchor_extracted_indices);
    Some(lis_len as f64 / anchor_extracted_indices.len() as f64)
}

/// Extracted-side indices of anchor tokens (tokens occurring exactly once on both sides),
/// ordered by their ground-truth position.
fn anchor_extracted_indices_by_gt_order(extracted_tokens: &[String], truth_tokens: &[String]) -> Vec<usize> {
    let extracted_counts = build_counts(extracted_tokens);
    let truth_counts = build_counts(truth_tokens);

    let mut extracted_index_by_token: HashMap<&str, usize> = HashMap::new();
    for (index, token) in extracted_tokens.iter().enumerate() {
        extracted_index_by_token.entry(token.as_str()).or_insert(index);
    }

    // `truth_tokens` is iterated in ground-truth order, so pushing in this order already
    // yields the ground-truth-ordered sequence — no separate sort by `gt_index` is needed.
    truth_tokens
        .iter()
        .filter(|token| truth_counts.get(token.as_str()).copied() == Some(1))
        .filter(|token| extracted_counts.get(token.as_str()).copied() == Some(1))
        .filter_map(|token| extracted_index_by_token.get(token.as_str()).copied())
        .collect()
}

/// Length of the longest strictly increasing subsequence, via patience sorting: `O(n log
/// n)` time and space rather than the naive `O(n^2)` DP.
fn longest_increasing_subsequence_len(sequence: &[usize]) -> usize {
    let mut pile_tops: Vec<usize> = Vec::new();
    for &value in sequence {
        match pile_tops.binary_search(&value) {
            Ok(_) => {}
            Err(insert_at) if insert_at == pile_tops.len() => pile_tops.push(value),
            Err(insert_at) => pile_tops[insert_at] = value,
        }
    }
    pile_tops.len()
}

/// Zero-width/invisible formatting characters that must never surface as, or silently fuse,
/// token content. NFKC normalization has no compatibility decomposition for these (they are
/// format characters, not letters), so they are stripped explicitly rather than relying on
/// the alphanumeric filter below to happen to exclude them. ~keep
const INVISIBLE_CHARACTERS: [char; 5] = [
    '\u{00ad}', // soft hyphen
    '\u{200b}', // zero width space
    '\u{200c}', // zero width non-joiner
    '\u{200d}', // zero width joiner
    '\u{feff}', // zero width no-break space / BOM
];

/// Tokenize text: NFKC-normalize, lowercase, split on whitespace (`|` also acts as a
/// separator so markdown table cell padding — `|a|b|` vs `| a | b |` — cannot change the
/// token stream), strip non-alphanumeric characters (preserving `.` and `,` only when
/// embedded between alphanumeric chars, e.g. "3.14", "3,14").
///
/// NFKC folds compatibility forms — fullwidth digits (`１２３` -> `123`), ligatures (`ﬁ` ->
/// `fi`) — into their canonical form so semantically identical text always tokenizes the same
/// regardless of source-encoding quirks. Applied BEFORE the alphanumeric filter. ~keep
pub fn tokenize(text: &str) -> Vec<String> {
    let text = strip_markdown_links(text);
    let text: String = text.chars().filter(|c| !INVISIBLE_CHARACTERS.contains(c)).collect();
    let text: String = text.nfkc().collect();
    let tokens = text
        .to_lowercase()
        .replace('|', " ")
        .split_whitespace()
        .map(|w| {
            let kept: String = w
                .chars()
                .filter(|c| c.is_alphanumeric() || *c == '.' || *c == ',')
                .collect();
            kept.trim_matches(|c: char| c == '.' || c == ',').to_string()
        })
        .filter(|w| !w.is_empty())
        .collect();
    tokenize_cjk_bigrams(tokens)
}

fn normalize_numeric_token(token: String) -> String {
    let digit_count = token.chars().filter(|c| c.is_ascii_digit()).count();
    if digit_count == 0 || digit_count > 15 {
        return token;
    }
    // Normalize thousands separators ("1,000" -> "1000") before the numeric parse so a
    // grouped number and its bare form become the same token. Only strip commas that form
    // well-shaped 3-digit groups, to avoid corrupting European decimals like "3,14". ~keep
    let candidate = if is_thousands_grouped(&token) {
        token.replace(',', "")
    } else {
        token.clone()
    };
    candidate
        .parse::<f64>()
        .map_or(token.clone(), |number| format!("{number}"))
}

/// Expand CJK script runs into overlapping bigrams while preserving non-CJK tokens.
///
/// The CJK accumulator is scoped to a single whitespace-delimited token (line/word) and is
/// always flushed at that boundary — bigrams never span a whitespace or line break. Bigrams
/// within a contiguous CJK run (including one interrupted only by decorative punctuation) are
/// still correct and desirable; bigrams welded across a line break are not, because that
/// erases the very reading-order/layout information the benchmark needs to score against —
/// two whitespace-adjacent CJK lines that were reordered or reassembled out of sequence would
/// otherwise contribute almost the same bigram multiset as the correctly-ordered document. ~keep
fn tokenize_cjk_bigrams(tokens: Vec<String>) -> Vec<String> {
    let mut result = Vec::new();
    for token in tokens {
        let mut cjk_run = String::new();
        let characters: Vec<char> = token.chars().collect();
        let mut start = 0;
        while start < characters.len() {
            let is_cjk = is_cjk_character(characters[start]);
            let mut end = start + 1;
            while end < characters.len() && is_cjk_character(characters[end]) == is_cjk {
                end += 1;
            }
            let run: String = characters[start..end].iter().collect();
            if is_cjk {
                cjk_run.push_str(&run);
            } else if run.chars().all(|character| matches!(character, '.' | ',')) {
                // Punctuation between CJK characters is decorative and must not split the run. ~keep
            } else {
                push_cjk_bigrams(&mut result, &mut cjk_run);
                result.push(normalize_numeric_token(run));
            }
            start = end;
        }
        push_cjk_bigrams(&mut result, &mut cjk_run);
    }
    result
}

fn push_cjk_bigrams(result: &mut Vec<String>, run: &mut String) {
    let characters: Vec<char> = run.chars().collect();
    if characters.len() == 1 {
        result.push(run.clone());
    } else {
        result.extend(characters.windows(2).map(|pair| pair.iter().collect()));
    }
    run.clear();
}

/// Whether a character belongs to a Chinese, Japanese, or Korean script block.
fn is_cjk_character(character: char) -> bool {
    matches!(
        character,
        '\u{1100}'..='\u{11ff}'
            | '\u{2e80}'..='\u{2eff}'
            // Japanese iteration marks such as `々` survive alphanumeric filtering; excluding
            // them would disable bigram expansion for an entire whitespace-free OCR line. ~keep
            | '\u{3000}'..='\u{303f}'
            | '\u{3040}'..='\u{30ff}'
            | '\u{3130}'..='\u{318f}'
            | '\u{31f0}'..='\u{31ff}'
            | '\u{3400}'..='\u{4dbf}'
            | '\u{4e00}'..='\u{9fff}'
            | '\u{a960}'..='\u{a97f}'
            | '\u{ac00}'..='\u{d7af}'
            | '\u{d7b0}'..='\u{d7ff}'
            | '\u{f900}'..='\u{faff}'
            | '\u{ff66}'..='\u{ff9f}'
            | '\u{20000}'..='\u{2fa1f}'
    )
}

/// Whether a numeric token uses `,` as a thousands separator in well-formed 3-digit groups
/// (e.g. `1,000`, `12,345,678`, `1,234.56`) — as opposed to a European decimal comma (`3,14`),
/// which must be left untouched.
fn is_thousands_grouped(token: &str) -> bool {
    let Some(int_part) = token.split('.').next() else {
        return false;
    };
    let groups: Vec<&str> = int_part.split(',').collect();
    if groups.len() < 2 {
        return false;
    }
    if groups[0].is_empty() || groups[0].len() > 3 || !groups[0].bytes().all(|b| b.is_ascii_digit()) {
        return false;
    }
    groups[1..]
        .iter()
        .all(|g| g.len() == 3 && g.bytes().all(|b| b.is_ascii_digit()))
}

/// Whether the ground truth contains any numeric tokens (used to gate the numeric-weighted
/// scoring formula). Deliberately checks the ground truth ONLY — see `compute_quality`'s
/// gating note for why "either side has numerics" over-penalizes extraction-only digits. ~keep
fn ground_truth_has_numeric_tokens(ground_truth: &str) -> bool {
    !filter_numeric(&tokenize(ground_truth)).is_empty()
}

/// Filter tokens to only those containing numeric characters (Unicode-aware)
fn filter_numeric(tokens: &[String]) -> Vec<String> {
    tokens
        .iter()
        .filter(|t| t.chars().any(|c| c.is_numeric()))
        .cloned()
        .collect()
}

/// Compute F1 score between two token bags using multiset intersection
pub fn compute_f1(extracted: &[String], truth: &[String]) -> f64 {
    if extracted.is_empty() && truth.is_empty() {
        return 1.0;
    }
    if extracted.is_empty() || truth.is_empty() {
        return 0.0;
    }

    let extracted_counts = build_counts(extracted);
    let truth_counts = build_counts(truth);

    let intersection: usize = truth_counts
        .iter()
        .map(|(token, &count)| {
            let ext_count = extracted_counts.get(token).copied().unwrap_or(0);
            ext_count.min(count)
        })
        .sum();

    let precision = intersection as f64 / extracted.len() as f64;
    let recall = intersection as f64 / truth.len() as f64;

    if precision + recall == 0.0 {
        return 0.0;
    }

    2.0 * precision * recall / (precision + recall)
}

/// Above this character length, char-level edit distance (O(n·m)) is too slow to
/// run per document, so the CER helpers report NaN rather than stall the bench.
const CER_MAX_CHARS: usize = 16_384;

/// Levenshtein edit distance between two char slices. Two-row DP: O(n·m) time,
/// O(min(n, m)) space.
fn edit_distance(a: &[char], b: &[char]) -> usize {
    let (a, b) = if a.len() < b.len() { (b, a) } else { (a, b) };
    if b.is_empty() {
        return a.len();
    }
    let mut prev: Vec<usize> = (0..=b.len()).collect();
    let mut curr = vec![0usize; b.len() + 1];
    for (i, &ca) in a.iter().enumerate() {
        curr[0] = i + 1;
        for (j, &cb) in b.iter().enumerate() {
            let substitution = prev[j] + usize::from(ca != cb);
            curr[j + 1] = substitution.min(prev[j + 1] + 1).min(curr[j] + 1);
        }
        std::mem::swap(&mut prev, &mut curr);
    }
    prev[b.len()]
}

/// Character error rate: edit distance normalized by the reference length. 0.0
/// is identical; values above 1.0 are possible when the hypothesis runs long.
/// Returns NaN for an empty reference or when either side exceeds
/// [`CER_MAX_CHARS`] (the metric is a report-only OCR diagnostic).
pub fn char_error_rate(reference: &str, hypothesis: &str) -> f64 {
    let reference_chars: Vec<char> = reference.chars().collect();
    let hypothesis_chars: Vec<char> = hypothesis.chars().collect();
    if reference_chars.is_empty() || reference_chars.len().max(hypothesis_chars.len()) > CER_MAX_CHARS {
        return f64::NAN;
    }
    edit_distance(&reference_chars, &hypothesis_chars) as f64 / reference_chars.len() as f64
}

/// Order- and character-sensitive text similarity in `[0, 1]`: `1 − distance /
/// max(len)`. 1.0 is identical, 0.0 fully dissimilar. Complements the
/// bag-of-words TF1 by penalizing transpositions and character-level OCR slips.
/// Returns NaN when both sides are empty or either exceeds [`CER_MAX_CHARS`].
pub fn normalized_edit_similarity(a: &str, b: &str) -> f64 {
    let a_chars: Vec<char> = a.chars().collect();
    let b_chars: Vec<char> = b.chars().collect();
    let max_len = a_chars.len().max(b_chars.len());
    if max_len == 0 || max_len > CER_MAX_CHARS {
        return f64::NAN;
    }
    1.0 - edit_distance(&a_chars, &b_chars) as f64 / max_len as f64
}

/// Build a token frequency map
fn build_counts(tokens: &[String]) -> HashMap<&str, usize> {
    let mut counts = HashMap::new();
    for token in tokens {
        *counts.entry(token.as_str()).or_insert(0) += 1;
    }
    counts
}

/// Compute token-level diff between extracted and ground truth token bags.
///
/// Returns (missing_tokens, extra_tokens) where:
/// - missing_tokens: tokens in GT with higher count than in extraction (recall misses)
/// - extra_tokens: tokens in extraction with higher count than in GT (precision misses)
///
/// Both are sorted by deficit/surplus count descending.
pub type TokenDiff = (Vec<(String, usize)>, Vec<(String, usize)>);

pub fn compute_token_diff(extracted: &[String], truth: &[String]) -> TokenDiff {
    let extracted_counts = build_counts(extracted);
    let truth_counts = build_counts(truth);

    let mut missing: Vec<(String, usize)> = truth_counts
        .iter()
        .filter_map(|(&token, &gt_count)| {
            let ext_count = extracted_counts.get(token).copied().unwrap_or(0);
            if gt_count > ext_count {
                Some((token.to_string(), gt_count - ext_count))
            } else {
                None
            }
        })
        .collect();
    missing.sort_by_key(|b| std::cmp::Reverse(b.1));

    let mut extra: Vec<(String, usize)> = extracted_counts
        .iter()
        .filter_map(|(&token, &ext_count)| {
            let gt_count = truth_counts.get(token).copied().unwrap_or(0);
            if ext_count > gt_count {
                Some((token.to_string(), ext_count - gt_count))
            } else {
                None
            }
        })
        .collect();
    extra.sort_by_key(|b| std::cmp::Reverse(b.1));

    (missing, extra)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_identical_text() {
        let text = "Hello world this is a test";
        let result = compute_quality(text, text);
        assert!((result.f1_score_text - 1.0).abs() < 0.001);
        assert!((result.quality_score - 1.0).abs() < 0.01);
    }

    #[test]
    fn test_completely_different() {
        let result = compute_quality("alpha beta gamma", "one two three");
        assert_eq!(result.f1_score_text, 0.0);
    }

    #[test]
    fn test_partial_overlap() {
        let result = compute_quality("hello world foo", "hello world bar");
        assert!((result.f1_score_text - 2.0 / 3.0).abs() < 0.001);
    }

    #[test]
    fn test_numeric_scoring() {
        let result = compute_quality("page 42 section 7", "page 42 section 7");
        assert!((result.f1_score_numeric - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_empty_inputs() {
        let result = compute_quality("", "");
        assert!((result.f1_score_text - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_empty_extracted() {
        let result = compute_quality("", "some ground truth");
        assert_eq!(result.f1_score_text, 0.0);
    }

    #[test]
    fn test_punctuation_stripped() {
        let result = compute_quality("hello, world!", "hello world");
        assert!((result.f1_score_text - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_case_insensitive() {
        let result = compute_quality("Hello World", "hello world");
        assert!((result.f1_score_text - 1.0).abs() < 0.001);
    }

    #[test]
    fn should_lose_only_the_four_true_boundary_bigrams_when_japanese_ocr_lines_are_reordered() {
        // Same fixture as before the CJK-bigram-boundary fix: a continuous two-sentence ground
        // truth vs. an extraction that OCR split into 5 lines and reassembled OUT OF their
        // true order. Before the fix, one global CJK accumulator spanned every line break, so
        // reassembling the (still internally-intact) chunks in the WRONG order fabricated 4
        // cross-line "seam" bigrams that happened to land as `extra_tokens`, while the metric
        // otherwise looked almost perfect (0.9444...) — reading-order damage was nearly
        // invisible. After the fix, no bigram is ever fabricated across a line break: precision
        // is now exactly 1.0 (every extracted bigram genuinely occurs somewhere in the ground
        // truth) because the extraction never claims a cross-line adjacency it doesn't have.
        // The 4 real seam bigrams that a correctly-ordered reassembly WOULD have produced
        // ("上か", "進め", "名の", "り横" — see below) are honestly reported as
        // `missing_tokens` (recall loss) instead of being silently smuggled in as a false
        // match. Bag-of-bigram F1 remains inherently insensitive to reordering whole intact
        // chunks (that is a property of any bag/multiset metric, not fixable by boundary
        // flushing alone), but the fix removes the specific defect: false-positive credit for
        // adjacencies that were never actually extracted. ~keep
        let ground_truth = concat!(
            "元来日本語は漢文に倣い、文字を上から下へ、また行を右から左へと進めて表記を行っていた。",
            "漢字と仮名の筆順も縦書きを前提としており、横書き不能な書体も存在する。"
        );
        let extracted = concat!(
            "から下へまた行を右から左へと進\n",
            "の筆順も縦書きを前提としており、\n",
            "めて表記を行っていた。漢字と仮名\n",
            "横書き不能な書体も存在する。\n",
            "元来日本語は漢文に倣い、文字を上"
        );

        let result = compute_quality(extracted, ground_truth);

        assert_eq!(result.f1_score_text, 0.9714285714285714);
        assert_eq!(result.quality_score, 0.9714285714285714);
        assert_eq!(
            result.extra_tokens.len(),
            0,
            "no bigram is ever fabricated across a line break"
        );
        assert_eq!(
            result.missing_tokens.len(),
            4,
            "the 4 true seam bigrams are honestly reported as missing, not smuggled in as extra"
        );
        // Bag-of-bigram F1 is inherently insensitive to reordering whole intact chunks — that
        // ceiling is a property of any bag/multiset metric, not something boundary-flushing
        // alone can fix (see `char_error_rate`/`normalized_edit_similarity` for the
        // order-sensitive diagnostics that do catch this). What the fix DOES guarantee is that
        // the score is no longer inflated by fabricated cross-line matches, above. ~keep
        assert!(result.correct, "0.9714... clears the 0.95 threshold; see comment above");
    }

    #[test]
    fn should_score_one_for_a_correctly_ordered_multiline_japanese_document() {
        // Regression guard for the fix above: when the extraction's line structure genuinely
        // matches the ground truth (i.e. nothing was reordered or reassembled differently),
        // flushing CJK bigrams at line boundaries must not introduce any spurious penalty. ~keep
        let text = concat!(
            "元来日本語は漢文に倣い、文字を上から下へ、また行を右から左へと進めて表記を行っていた。\n",
            "漢字と仮名の筆順も縦書きを前提としており、横書き不能な書体も存在する。"
        );

        let result = compute_quality(text, text);

        assert_eq!(result.f1_score_text, 1.0);
        assert_eq!(result.quality_score, 1.0);
        assert!(result.correct);
    }

    #[test]
    fn should_not_award_partial_credit_for_unrelated_cjk_strings() {
        let result = compute_quality("日本語の文書", "本日の歴史");

        assert_eq!(result.f1_score_text, 0.0);
    }

    #[test]
    fn should_use_cjk_bigrams_with_single_character_fallback() {
        assert_eq!(tokenize("日本語"), vec!["日本", "本語"]);
        assert_eq!(tokenize("日"), vec!["日"]);
    }

    #[test]
    fn should_treat_a_line_break_between_cjk_characters_as_a_bigram_boundary() {
        // Previously named `should_ignore_line_wrapping_between_cjk_characters` and asserted
        // `wrapped == continuous` — that "decorative OCR line wrap" bridging is exactly the D2
        // defect: it let a single CJK accumulator span the whole document, silently absorbing
        // reordered/reassembled line breaks along with genuine mid-word OCR wraps. A CJK
        // bigram must never cross a whitespace/line break, full stop; this fixture's wrapped
        // form now tokenizes to fewer, shorter runs than the continuous form. ~keep
        let continuous = tokenize("日本語の文書");
        let wrapped = tokenize("日本\n語の\n文書");

        assert_eq!(continuous, vec!["日本", "本語", "語の", "の文", "文書"]);
        assert_eq!(wrapped, vec!["日本", "語の", "文書"]);
        assert_ne!(wrapped, continuous);

        let result = compute_quality("日本\n語の\n文書", "日本語の文書");
        assert_eq!(result.f1_score_text, 0.7499999999999999);
        assert_eq!(result.quality_score, 0.7499999999999999);
    }

    #[test]
    fn should_ignore_decorative_punctuation_between_cjk_characters() {
        assert_eq!(tokenize("日本,語"), tokenize("日本語"));
        assert_eq!(tokenize("日本。語"), tokenize("日本語"));
    }

    #[test]
    fn should_tokenize_cjk_around_embedded_latin_and_numeric_runs() {
        assert_eq!(
            tokenize("日本語HS令和5年"),
            vec!["日本", "本語", "hs", "令和", "5", "年"]
        );
    }

    #[test]
    fn should_keep_iteration_marks_inside_whitespace_free_japanese_bigrams() {
        assert_eq!(tokenize("時々刻々"), vec!["時々", "々刻", "刻々"]);
    }

    #[test]
    fn should_preserve_latin_whitespace_tokenization() {
        assert_eq!(tokenize("Hello, world! 15.0"), vec!["hello", "world", "15"]);
    }

    #[test]
    fn should_normalize_fullwidth_digits_to_ascii_via_nfkc() {
        // D3: fullwidth digits are outside `is_cjk_character`'s ranges and outside the
        // ASCII-digit count `normalize_numeric_token` checks, so without NFKC they never
        // matched their ASCII-equivalent form. ~keep
        assert_eq!(tokenize("１２３"), tokenize("123"));
        assert_eq!(tokenize("１２３"), vec!["123"]);
    }

    #[test]
    fn should_normalize_ligatures_to_ascii_via_nfkc() {
        // D3: "ﬁ" is alphabetic so it silently survived the alphanumeric filter as its own
        // literal glyph instead of matching "fi", corrupting f1_score_text for a correct
        // extraction. ~keep
        assert_eq!(tokenize("ﬁle"), tokenize("file"));
        assert_eq!(tokenize("ﬁle"), vec!["file"]);
    }

    #[test]
    fn should_ignore_a_soft_hyphen_within_a_word() {
        assert_eq!(tokenize("exam\u{ad}ple"), tokenize("example"));
        assert_eq!(tokenize("exam\u{ad}ple"), vec!["example"]);
    }

    #[test]
    fn test_tokenize_number_normalization() {
        let tokens_a = tokenize("15.0");
        let tokens_b = tokenize("15");
        assert_eq!(tokens_a, tokens_b, "15.0 and 15 should normalize to the same token");
        assert_eq!(tokens_a, vec!["15"]);

        assert_eq!(tokenize("100.00"), vec!["100"]);
    }

    #[test]
    fn test_compute_f1_number_equivalence() {
        let extracted = tokenize("price 15.0 dollars");
        let truth = tokenize("price 15 dollars");
        let f1 = compute_f1(&extracted, &truth);
        assert!(
            (f1 - 1.0).abs() < 0.001,
            "F1 should be 1.0 for semantically equivalent numeric tokens, got {f1}"
        );
    }

    #[test]
    fn test_tokenize_preserves_decimals() {
        assert_eq!(tokenize("3.14"), vec!["3.14"]);
        assert_eq!(tokenize("0.5"), vec!["0.5"]);
        assert_eq!(tokenize("12.345"), vec!["12.345"]);
    }

    #[test]
    fn test_no_numbers_no_boost() {
        let result = compute_quality("hello world foo", "hello world bar");
        let expected_text_f1 = 2.0 / 3.0;
        assert!(
            (result.f1_score_text - expected_text_f1).abs() < 0.001,
            "text F1 should be 2/3, got {}",
            result.f1_score_text
        );
        assert!(
            (result.quality_score - expected_text_f1).abs() < 0.001,
            "quality_score should equal text F1 ({expected_text_f1}) when no numbers, got {}",
            result.quality_score
        );
    }

    #[test]
    fn should_score_text_only_when_ground_truth_has_no_numeric_tokens_despite_a_stray_extracted_digit() {
        // D1: the ground truth has zero digits, but the extraction emits a stray page number.
        // Numeric fidelity cannot be measured against a reference with no numbers, so
        // quality_score must equal the text-only F1 — not collapse through the 0.6/0.4 split,
        // which would apply a 40% penalty for a header/footer convention mismatch. ~keep
        let ground_truth = "revenue grew significantly year over year";
        let extracted = "revenue grew significantly year over year Page 3";

        let result = compute_quality(extracted, ground_truth);

        assert_eq!(
            result.f1_score_numeric, 0.0,
            "no numeric tokens in GT ⇒ numeric F1 is vacuously 0"
        );
        assert_eq!(
            result.quality_score, result.f1_score_text,
            "quality_score must equal text-only F1 when the ground truth has no numeric tokens"
        );
        assert_eq!(result.f1_score_text, 0.8571428571428571);
        // The stray digit is not unpunished: it still costs precision inside f1_score_text.
        assert!(result.quality_score < 1.0);
    }

    #[test]
    fn should_still_score_numeric_zero_when_ground_truth_has_digits_extraction_drops() {
        // D1's asymmetry: when the ground truth DOES contain numerics and the extraction
        // dropped them, that is a genuine failure and must still cost the full 40% numeric
        // weight, unlike the no-numeric-in-GT case above. ~keep
        let ground_truth = "revenue was 42 million dollars";
        let extracted = "revenue was million dollars";

        let result = compute_quality(extracted, ground_truth);

        assert_eq!(result.f1_score_numeric, 0.0);
        assert_eq!(result.quality_score, 0.6 * result.f1_score_text);
        assert_ne!(
            result.quality_score, result.f1_score_text,
            "unlike the no-GT-numerics case, this must NOT equal the text-only score"
        );
    }

    #[test]
    fn should_gate_combined_scoring_numeric_weight_on_ground_truth_only() {
        // Same D1 gating principle applied to `compute_quality_with_structure`'s combined
        // 0.5/0.2/0.3 formula: a stray extracted digit with no ground-truth numerics must not
        // pull in the numeric-weighted branch either. ~keep
        let ground_truth = "# Title\n\nParagraph text here.";
        let extracted = "# Title\n\nParagraph text here. 7";

        let metrics =
            compute_quality_with_structure(extracted, ground_truth, Some(ground_truth), OutputFormat::Markdown);
        let structural_f1 = structural_sidecar::score_markdown(extracted, ground_truth).sf1;
        let expected = 0.625 * metrics.f1_score_text + 0.375 * structural_f1;

        assert_eq!(metrics.f1_score_numeric, 0.0);
        assert_eq!(metrics.quality_score, expected);
    }

    #[test]
    fn test_url_stripped_from_tokens() {
        let tokens = tokenize("[link text](https://example.com)");
        assert_eq!(tokens, vec!["link", "text"]);

        let tokens = tokenize("![alt text](https://example.com/image.png)");
        assert_eq!(tokens, vec!["alt", "text"]);

        let tokens = tokenize("See [docs](https://example.com/docs) for details");
        assert_eq!(tokens, vec!["see", "docs", "for", "details"]);
    }

    #[test]
    fn should_strip_bare_urls_same_as_markdown_linked_urls() {
        // D4: a bare url used to survive the alphanumeric filter as junk
        // (`https://example.com` -> `httpsexamplecom`), so a framework emitting bare links took
        // a precision hit that a framework emitting markdown links did not. Both must discard
        // the url identically — compared here against an empty-anchor-text markdown link, which
        // discards the same amount of surrounding link text (none) as the bare form. ~keep
        let bare = tokenize("See https://example.com for details");
        let linked = tokenize("See [](https://example.com) for details");

        assert_eq!(bare, vec!["see", "for", "details"]);
        assert_eq!(bare, linked);
        assert_eq!(tokenize("Visit www.example.com today"), vec!["visit", "today"]);

        let ground_truth = tokenize("See for details");
        assert_eq!(compute_f1(&bare, &ground_truth), 1.0);
    }

    #[test]
    fn should_score_pipe_table_cell_padding_identically() {
        // D4: `|` must act as a token separator so markdown table cell padding style cannot
        // change the token stream — `|a|b|` and `| a | b |` are semantically identical tables
        // and must tokenize (and therefore score) identically. ~keep
        assert_eq!(tokenize("|a|b|"), tokenize("| a | b |"));
        assert_eq!(tokenize("|a|b|"), vec!["a", "b"]);

        let ground_truth = "a b";
        let tight = compute_quality("|a|b|", ground_truth);
        let padded = compute_quality("| a | b |", ground_truth);
        assert_eq!(tight.f1_score_text, padded.f1_score_text);
        assert_eq!(tight.quality_score, padded.quality_score);
        assert_eq!(tight.f1_score_text, 1.0);
    }

    #[test]
    fn test_large_number_preserved() {
        let tokens = tokenize("10000000000000001");
        assert_eq!(
            tokens,
            vec!["10000000000000001"],
            "17-digit number should be preserved as-is, not rounded by f64"
        );

        let tokens = tokenize("12345678901234.0");
        assert_eq!(
            tokens,
            vec!["12345678901234"],
            "15-digit number with trailing .0 should still normalize"
        );
    }

    #[test]
    fn test_thousands_separators_normalize_to_bare_number() {
        // "1,000" and "1000" must tokenize identically (previously "1,000" failed f64 parse). ~keep
        assert_eq!(tokenize("1,000"), tokenize("1000"));
        assert_eq!(tokenize("12,345,678"), tokenize("12345678"));
        assert_eq!(tokenize("1,234.56"), tokenize("1234.56"));
        // A European-decimal comma (2-digit group) must NOT be treated as a thousands separator. ~keep
        assert_eq!(tokenize("3,14"), vec!["3,14"]);
    }

    #[test]
    fn structured_quality_uses_canonical_sf1() {
        let extracted = "# Title\n\nParagraph.\n\n- first\n- second";
        let ground_truth = "## Title\n\nParagraph.\n\n1. first\n2. second";
        let expected = structural_sidecar::score_markdown(extracted, ground_truth).sf1;

        let metrics = compute_quality_with_structure(
            extracted,
            "Title Paragraph first second",
            Some(ground_truth),
            OutputFormat::Markdown,
        );

        assert_eq!(metrics.f1_score_layout, Some(expected));
    }

    #[test]
    fn edit_distance_counts_single_edits() {
        let chars = |s: &str| s.chars().collect::<Vec<_>>();
        assert_eq!(edit_distance(&chars("kitten"), &chars("sitting")), 3);
        assert_eq!(edit_distance(&chars(""), &chars("abc")), 3);
        assert_eq!(edit_distance(&chars("abc"), &chars("abc")), 0);
    }

    #[test]
    fn normalized_edit_similarity_is_order_sensitive() {
        assert!((normalized_edit_similarity("hello world", "hello world") - 1.0).abs() < 1e-9);
        let sim = normalized_edit_similarity("ab", "ba");
        assert!(
            (0.0..1.0).contains(&sim),
            "transposition should lower similarity, got {sim}"
        );
        assert!((normalized_edit_similarity("abcdefghij", "abcdefghiX") - 0.9).abs() < 1e-9);
    }

    #[test]
    fn char_metrics_guard_empty_and_oversized_inputs() {
        assert!(char_error_rate("", "anything").is_nan(), "empty reference ⇒ NaN CER");
        assert!(
            normalized_edit_similarity("", "").is_nan(),
            "empty pair ⇒ NaN similarity"
        );
        let huge = "a".repeat(CER_MAX_CHARS + 1);
        assert!(
            normalized_edit_similarity(&huge, &huge).is_nan(),
            "oversized input is skipped"
        );
        assert!(
            (char_error_rate("abcd", "abXd") - 0.25).abs() < 1e-9,
            "one of four chars wrong ⇒ 0.25"
        );
    }

    // --- reading_order_score (report-only) ---

    #[test]
    fn min_reading_order_anchors_starts_at_eight() {
        assert_eq!(MIN_READING_ORDER_ANCHORS, 8);
    }

    #[test]
    fn should_score_reading_order_one_for_identical_documents() {
        let text = "alpha beta gamma delta epsilon zeta eta theta iota kappa";
        assert_eq!(reading_order_score(text, text), Some(1.0));
    }

    #[test]
    fn should_score_reading_order_near_zero_for_fully_reversed_token_order() {
        // 10 unique anchors in fully reversed order. Any strictly decreasing sequence has
        // a Longest Increasing Subsequence of length exactly 1, so the score is exactly
        // 1/10 = 0.1.
        let ground_truth = "one two three four five six seven eight nine ten";
        let extracted = "ten nine eight seven six five four three two one";
        assert_eq!(reading_order_score(extracted, ground_truth), Some(0.1));
    }

    #[test]
    fn should_score_reading_order_one_for_correctly_ordered_japanese_document() {
        // Same fixture as `should_score_one_for_a_correctly_ordered_multiline_japanese_document`.
        let text = concat!(
            "元来日本語は漢文に倣い、文字を上から下へ、また行を右から左へと進めて表記を行っていた。\n",
            "漢字と仮名の筆順も縦書きを前提としており、横書き不能な書体も存在する。"
        );
        assert_eq!(reading_order_score(text, text), Some(1.0));
    }

    #[test]
    fn should_score_reading_order_far_below_bag_of_tokens_f1_for_scrambled_japanese_lines() {
        // Same 5-line-reordered fixture as
        // `should_lose_only_the_four_true_boundary_bigrams_when_japanese_ocr_lines_are_reordered`,
        // where f1_score_text scores 0.9714285714285714 on this scrambled document — bag-of-
        // tokens F1 is order-insensitive by construction and cannot see the reordering at all.
        // reading_order_score exists precisely to catch what f1_score_text cannot: on the exact
        // same input it must be, and is, dramatically lower. ~keep
        let ground_truth = concat!(
            "元来日本語は漢文に倣い、文字を上から下へ、また行を右から左へと進めて表記を行っていた。",
            "漢字と仮名の筆順も縦書きを前提としており、横書き不能な書体も存在する。"
        );
        let extracted = concat!(
            "から下へまた行を右から左へと進\n",
            "の筆順も縦書きを前提としており、\n",
            "めて表記を行っていた。漢字と仮名\n",
            "横書き不能な書体も存在する。\n",
            "元来日本語は漢文に倣い、文字を上"
        );

        let score = reading_order_score(extracted, ground_truth);

        assert_eq!(score, Some(0.578125));
        let f1_text_on_same_fixture = 0.9714285714285714;
        assert!(
            score.unwrap() < f1_text_on_same_fixture - 0.3,
            "reading_order_score ({score:?}) must be dramatically lower than the bag-of-tokens \
             f1 ({f1_text_on_same_fixture}) on this scrambled fixture — that contrast is the \
             entire point of this metric"
        );
    }

    #[test]
    fn should_return_none_when_fewer_than_minimum_anchors_are_found() {
        // "hello" and "world" each occur exactly once on both sides, but 2 anchors is below
        // MIN_READING_ORDER_ANCHORS (8) — insufficient signal to report a number.
        assert_eq!(reading_order_score("hello world", "hello world"), None);
    }

    #[test]
    fn should_return_none_for_empty_extraction_or_empty_ground_truth() {
        let has_tokens = "some ground truth with plenty of unique tokens listed right here";
        assert_eq!(reading_order_score("", has_tokens), None);
        assert_eq!(reading_order_score(has_tokens, ""), None);
        assert_eq!(reading_order_score("", ""), None);
    }

    #[test]
    fn should_not_panic_and_return_none_for_heavily_repetitive_tokens() {
        // Every token repeats, so there are zero anchors (an anchor requires an exactly-once
        // occurrence on both sides) — must return None cleanly rather than panic or divide by
        // zero.
        let repetitive = "the the the the the the the the the the the the";
        assert_eq!(reading_order_score(repetitive, repetitive), None);
    }

    #[test]
    fn should_expose_reading_order_score_via_compute_quality_without_moving_quality_score() {
        // The whole point of this metric: bag-of-tokens F1 is 1.0 (every token survives, just
        // reordered), while reading_order_score correctly flags the reordering. quality_score
        // must stay exactly what it was before this metric existed — REPORT-ONLY.
        let ground_truth = "one two three four five six seven eight nine ten";
        let extracted = "ten nine eight seven six five four three two one";

        let result = compute_quality(extracted, ground_truth);

        assert_eq!(result.f1_score_text, 1.0);
        assert_eq!(result.reading_order_score, Some(0.1));
        assert_eq!(
            result.quality_score, 1.0,
            "reading_order_score is report-only and must not move quality_score"
        );
    }
}
