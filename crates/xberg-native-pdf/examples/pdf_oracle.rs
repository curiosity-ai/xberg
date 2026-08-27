//! PDF oracle: a deterministic text+status snapshot of every PDF under `test_documents/`.
//!
//! This is the measuring instrument for the PDF performance work (commit C0 of that
//! series). Every later commit that claims a speedup, or claims "no behavior change",
//! is judged against a diff of two runs of this tool -- one from before the change, one
//! from after.
//!
//! # Why both text AND status
//!
//! An external profiling spike on a sibling PDF engine reported a large win from a
//! change to object-header reading. Re-measured against a real regression it introduced
//! on a malformed fixture: the fixture went from extracting OK to failing with "page
//! index 0 not found" -- a STATUS change, not a text change. A tool that only hashes
//! extracted text would have missed that regression entirely, because the failing case
//! never produces text to hash in the first place. So every row captures the exact
//! `Result` outcome (`Ok` vs. the specific `Error` variant) at both the document level
//! (`page_count()`) and the page level (`extract_page_text_with_options`), not just a
//! hash of the happy path.
//!
//! # What a clean diff PROVES
//!
//! - No document's `page_count()` outcome changed: same `Ok(n)`, or the same `Error`
//!   variant (including its fields) if it still fails.
//! - No page's `extract_page_text_with_options(index, ColumnAware)` outcome changed:
//!   same extracted text (via its SHA-256), or the same `Error` variant if it still
//!   fails. A panic partway through extraction is caught and recorded as its own
//!   status (`ERROR:Panic(...)`), so it participates in the diff instead of aborting
//!   the run.
//!
//! # What a clean diff does NOT prove
//!
//! - That anything got FASTER. This TSV never contains wall-clock data; see `--timing`.
//! - That reading orders other than `ColumnAware` are unaffected -- only `ColumnAware`
//!   is sampled here.
//! - That the extracted text is CORRECT, only that it is UNCHANGED. A pre-existing bug
//!   reproduces as a clean diff.
//! - Cross-machine reproducibility of `Io` error text: `std::io::Error`'s `Debug` output
//!   can embed OS-specific strings. Two runs on the SAME machine against the SAME tree
//!   are always byte-identical; that is the comparison this tool is built for.
//!
//! # Modes
//!
//! - default: writes the text+status TSV described above to stdout.
//! - `--timing`: same TSV to stdout, PLUS one `path\twall_time_micros` row per document
//!   written to stderr. Timing is kept off stdout, in a separate stream, so redirecting
//!   stdout to a file for diffing is never polluted by wall-clock noise. Use it to state
//!   a LOCAL, corpus-relative speedup instead of quoting a number measured elsewhere.
//! - `--headers`: a different measurement entirely -- for every `N G obj` header found
//!   in the raw file bytes, how many bytes would be read to reach the next LF from that
//!   header's start (mirroring `PdfDocument::load_uncompressed_object_impl`'s first
//!   `read_until(b'\n', ..)` call over the object header), versus how many bytes are
//!   actually needed to recognize the header (through the end of the `obj` keyword).
//!   Summed per document, this is what makes a claim like "N MB read to obtain M MB of
//!   headers" checkable in-repo instead of living in an external gist. The header scan
//!   here is an independent byte-level regex over the raw file, not a call into the
//!   crate's private xref-driven object walk (examples only see the crate's public
//!   API), so it can overcount if binary stream content happens to match `N G obj`;
//!   that overcounting is itself deterministic and its order of magnitude has been
//!   checked against the internal function's structure (see the implementer's report).
//!
//! # Usage
//!
//! ```text
//! cargo run -p xberg-native-pdf --example pdf_oracle > before.tsv
//! # ... make a change ...
//! cargo run -p xberg-native-pdf --example pdf_oracle > after.tsv
//! diff before.tsv after.tsv   # empty output == no observable regression
//!
//! cargo run -p xberg-native-pdf --example pdf_oracle -- --timing > before.tsv 2> before.timing.tsv
//! cargo run -p xberg-native-pdf --example pdf_oracle -- --timing > after.tsv 2> after.timing.tsv
//! diff before.tsv after.tsv                 # correctness (must stay empty)
//! # compare before.timing.tsv / after.timing.tsv by hand for the speedup claim
//!
//! cargo run -p xberg-native-pdf --example pdf_oracle -- --headers > headers.tsv
//! ```
//!
//! Requires the `test_documents/` git submodule to be populated (`python3
//! fetch_corpus.py`, or `git submodule update --init test_documents`); a missing or
//! empty corpus is reported with a clear message and a non-zero exit code rather than
//! silently emitting an empty (vacuously "clean") TSV.

use std::fs;
use std::io::{self, Write as _};
use std::panic::{self, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::time::Instant;

use regex::bytes::{Regex, RegexBuilder};
use sha2::{Digest, Sha256};
use xberg_native_pdf::{PdfDocument, ReadingOrder};

fn main() {
    install_quiet_panic_hook();

    let mut timing = false;
    let mut headers = false;
    for arg in std::env::args().skip(1) {
        match arg.as_str() {
            "--timing" => timing = true,
            "--headers" => headers = true,
            other => {
                let _ = writeln!(
                    io::stderr(),
                    "pdf_oracle: unknown argument '{other}' (expected --timing or --headers)"
                );
                std::process::exit(2);
            }
        }
    }
    if timing && headers {
        let _ = writeln!(
            io::stderr(),
            "pdf_oracle: --timing and --headers are separate modes; pass only one"
        );
        std::process::exit(2);
    }

    let repo_root = match find_repo_root() {
        Ok(root) => root,
        Err(message) => {
            let _ = writeln!(io::stderr(), "pdf_oracle: {message}");
            std::process::exit(1);
        }
    };

    let corpus_dir = repo_root.join("test_documents");
    let pdf_paths = match collect_pdf_paths(&corpus_dir) {
        Ok(paths) => paths,
        Err(error) => {
            let _ = writeln!(
                io::stderr(),
                "pdf_oracle: could not read {} ({error}).\n\
                 Run `python3 fetch_corpus.py` to fetch the test_documents/ corpus first.",
                corpus_dir.display()
            );
            std::process::exit(1);
        }
    };

    if pdf_paths.is_empty() {
        let _ = writeln!(
            io::stderr(),
            "pdf_oracle: no *.pdf files found under {}.\n\
             The test_documents/ submodule looks uninitialized or empty.\n\
             Run `python3 fetch_corpus.py` (or `git submodule update --init test_documents`) first.",
            corpus_dir.display()
        );
        std::process::exit(1);
    }

    let mut documents: Vec<(String, PathBuf)> = pdf_paths
        .into_iter()
        .map(|path| (repo_relative(&path, &repo_root), path))
        .collect();
    // Sort by the repo-relative string, not filesystem order, so output ordering is
    // stable across platforms and directory-entry orderings. ~keep
    documents.sort_by(|a, b| a.0.cmp(&b.0));

    let stdout = io::stdout();
    let mut out = io::BufWriter::new(stdout.lock());

    if headers {
        run_headers(&documents, &mut out);
        return;
    }

    run_oracle(&documents, &mut out, timing);
}

/// Suppress the default panic hook's stderr dump.
///
/// Panics inside a single document's extraction are caught per-page in
/// [`measure_document`] and recorded as data (an `ERROR:Panic(..)` cell), so the
/// default hook printing the same panic to stderr would just be duplicate noise on
/// every run -- and would make `--timing`'s stderr stream (meant for timing rows only)
/// unreadable.
fn install_quiet_panic_hook() {
    panic::set_hook(Box::new(|_info| {}));
}

fn panic_message(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(message) = payload.downcast_ref::<&str>() {
        (*message).to_string()
    } else if let Some(message) = payload.downcast_ref::<String>() {
        message.clone()
    } else {
        "non-string panic payload".to_string()
    }
}

/// Escape TSV-hostile bytes (tab, CR, LF, backslash) in dynamic text such as an
/// `Error`'s `Debug` output, which can embed raw fragments of a malformed document.
fn escape_field(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('\t', "\\t")
        .replace('\n', "\\n")
        .replace('\r', "\\r")
}

fn to_hex(bytes: &[u8]) -> String {
    const HEX_DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut hex = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        hex.push(HEX_DIGITS[(byte >> 4) as usize] as char);
        hex.push(HEX_DIGITS[(byte & 0x0f) as usize] as char);
    }
    hex
}

/// Repo root is the nearest ancestor of this crate that has a `test_documents` entry
/// (present even before the submodule is populated, since git still creates the
/// directory). Derived from the compile-time `CARGO_MANIFEST_DIR`, so it does not
/// depend on the directory the binary happens to be invoked from.
fn find_repo_root() -> Result<PathBuf, String> {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    for ancestor in manifest_dir.ancestors() {
        if ancestor.join("test_documents").exists() {
            return Ok(ancestor.to_path_buf());
        }
    }
    Err(format!(
        "could not find a test_documents/ directory above {} (searched all parent directories)",
        manifest_dir.display()
    ))
}

fn collect_pdf_paths(root: &Path) -> io::Result<Vec<PathBuf>> {
    let mut found = Vec::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for entry in fs::read_dir(&dir)? {
            let entry = entry?;
            let file_type = entry.file_type()?;
            let path = entry.path();
            if file_type.is_dir() {
                stack.push(path);
            } else if file_type.is_file() {
                let is_pdf = path
                    .extension()
                    .and_then(|ext| ext.to_str())
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("pdf"));
                if is_pdf {
                    found.push(path);
                }
            }
        }
    }
    Ok(found)
}

/// Repo-relative, forward-slash-joined path -- stable across platforms and never an
/// absolute path (which would make the TSV useless for cross-machine diffing).
fn repo_relative(path: &Path, root: &Path) -> String {
    let relative = path.strip_prefix(root).unwrap_or(path);
    relative
        .components()
        .map(|component| component.as_os_str().to_string_lossy().into_owned())
        .collect::<Vec<_>>()
        .join("/")
}

fn run_oracle(documents: &[(String, PathBuf)], out: &mut impl io::Write, timing: bool) {
    let stderr = io::stderr();
    let mut timing_out = io::BufWriter::new(stderr.lock());

    writeln!(out, "path\tpage_count\tpages").expect("write oracle header");
    if timing {
        writeln!(timing_out, "path\twall_time_micros").expect("write timing header");
    }

    for (relative, path) in documents {
        let start = timing.then(Instant::now);
        let (page_count_field, pages_field) = measure_document(path);
        if let Some(start) = start {
            writeln!(timing_out, "{relative}\t{}", start.elapsed().as_micros()).expect("write timing row");
        }
        writeln!(out, "{relative}\t{page_count_field}\t{pages_field}").expect("write oracle row");
    }
}

/// Returns `(page_count_field, pages_field)` for one document.
///
/// `page_count_field` is a decimal count, `ERROR:<Debug of the Error variant>`, or
/// `ERROR:Panic(<message>)`. `pages_field` is empty unless the document opened and
/// `page_count()` succeeded, in which case it holds `index=cell` entries joined by
/// `;`, one per page, where `cell` is a SHA-256 hex digest of that page's spans' text
/// joined with `\n`, or that page's own `ERROR:...` status.
///
/// Each of open / page_count / per-page extraction is wrapped in its own
/// `catch_unwind` so a panic on one page (or on open) is recorded as data instead of
/// aborting the whole corpus walk, and so pages before a panicking page keep their
/// real results. A panic deliberately is not treated as fatal to the *document*: the
/// crate's `RefCell`-backed caches are not proven consistent after an unwind, so
/// results for later pages of the *same* document after a caught panic are still
/// deterministic (same input, same code) but not independently meaningful -- exactly
/// the situation this tool exists to surface, not to paper over.
fn measure_document(path: &Path) -> (String, String) {
    let open_result = panic::catch_unwind(AssertUnwindSafe(|| PdfDocument::open(path)));
    let document = match open_result {
        Ok(Ok(document)) => document,
        Ok(Err(error)) => return (format!("ERROR:{}", escape_field(&format!("{error:?}"))), String::new()),
        Err(payload) => {
            return (
                format!("ERROR:Panic({})", escape_field(&panic_message(payload))),
                String::new(),
            );
        }
    };

    let count_result = panic::catch_unwind(AssertUnwindSafe(|| document.page_count()));
    let page_count = match count_result {
        Ok(Ok(count)) => count,
        Ok(Err(error)) => return (format!("ERROR:{}", escape_field(&format!("{error:?}"))), String::new()),
        Err(payload) => {
            return (
                format!("ERROR:Panic({})", escape_field(&panic_message(payload))),
                String::new(),
            );
        }
    };

    let mut pages = Vec::with_capacity(page_count);
    for index in 0..page_count {
        let page_result = panic::catch_unwind(AssertUnwindSafe(|| {
            document.extract_page_text_with_options(index, ReadingOrder::ColumnAware)
        }));
        let cell = match page_result {
            Ok(Ok(page_text)) => {
                let joined = page_text
                    .spans
                    .iter()
                    .map(|span| span.text.as_str())
                    .collect::<Vec<_>>()
                    .join("\n");
                let digest = Sha256::digest(joined.as_bytes());
                to_hex(digest.as_slice())
            }
            Ok(Err(error)) => format!("ERROR:{}", escape_field(&format!("{error:?}"))),
            Err(payload) => format!("ERROR:Panic({})", escape_field(&panic_message(payload))),
        };
        pages.push(format!("{index}={cell}"));
    }

    (page_count.to_string(), pages.join(";"))
}

fn run_headers(documents: &[(String, PathBuf)], out: &mut impl io::Write) {
    // ASCII-only, byte-oriented: PDFs are binary, so the haystack is not valid UTF-8
    // and unicode-mode `\d`/`\b` semantics would be both wrong and slower. ~keep
    let header_pattern: Regex = RegexBuilder::new(r"[0-9]+[ \t\r\n]+[0-9]+[ \t\r\n]+obj\b")
        .unicode(false)
        .build()
        .expect("header_pattern is a fixed, valid regex");

    writeln!(
        out,
        "path\theader_count\tbytes_to_lf_total\tbytes_needed_total\tmax_bytes_to_lf\tstatus"
    )
    .expect("write headers header");

    for (relative, path) in documents {
        let stats = measure_headers(path, &header_pattern);
        writeln!(
            out,
            "{relative}\t{}\t{}\t{}\t{}\t{}",
            stats.count, stats.to_lf_total, stats.needed_total, stats.max_to_lf, stats.status
        )
        .expect("write headers row");
    }
}

struct HeaderStats {
    count: u64,
    to_lf_total: u64,
    needed_total: u64,
    max_to_lf: u64,
    status: String,
}

/// For every `N G obj` header found by `pattern`, measure two quantities from the
/// header's start offset:
///
/// - `to_lf`: bytes up to and including the next `\n`, mirroring the byte count
///   `load_uncompressed_object_impl`'s first `reader.read_until(b'\n', ..)` call would
///   consume if it started at this offset. When a header's line runs deep into
///   subsequent (often binary) content before the next `\n`, this overshoots the
///   handful of bytes the header itself occupies.
/// - `needed`: bytes from the header's start through the end of the `obj` keyword --
///   all a header-recognition pass actually has to look at.
///
/// Both are summed (and `to_lf`'s maximum tracked) per document rather than emitted
/// per header, because the amplification this is meant to make checkable is a
/// corpus/document-level ratio (e.g. "22.0 MB read to obtain 4.8 MB of headers"), and
/// because a single pathological document can have `to_lf_total` exceed its own file
/// size: many headers each independently overshoot into the same downstream bytes.
fn measure_headers(path: &Path, pattern: &Regex) -> HeaderStats {
    let data = match fs::read(path) {
        Ok(data) => data,
        Err(error) => {
            return HeaderStats {
                count: 0,
                to_lf_total: 0,
                needed_total: 0,
                max_to_lf: 0,
                status: format!("ERROR:{}", escape_field(&format!("{error:?}"))),
            };
        }
    };

    let mut stats = HeaderStats {
        count: 0,
        to_lf_total: 0,
        needed_total: 0,
        max_to_lf: 0,
        status: "ok".to_string(),
    };

    for header in pattern.find_iter(&data) {
        let start = header.start();
        let needed = (header.end() - start) as u64;
        let to_lf = match data[start..].iter().position(|&byte| byte == b'\n') {
            Some(offset) => (offset + 1) as u64,
            None => (data.len() - start) as u64,
        };

        stats.count += 1;
        stats.to_lf_total += to_lf;
        stats.needed_total += needed;
        stats.max_to_lf = stats.max_to_lf.max(to_lf);
    }

    stats
}
