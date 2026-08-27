//! Archive extraction functionality.
//!
//! This module provides functions for extracting file lists and contents from archives.
//! Supported formats:
//! - ZIP archives
//! - TAR archives (including compressed TAR.GZ, TAR.BZ2)
//! - 7Z archives
//! - GZIP archives
//!
//! Each format has its own submodule with specialized extraction logic.

mod gzip;
mod sevenz;
mod tar;
mod zip;

pub(crate) use gzip::extract_gzip_with_bytes;
#[cfg(test)]
pub(crate) use gzip::{decompress_gzip, extract_gzip, extract_gzip_metadata, extract_gzip_text_content};
pub(crate) use sevenz::{extract_7z_file_bytes, extract_7z_metadata, extract_7z_text_content};
pub(crate) use tar::{extract_tar_file_bytes, extract_tar_metadata, extract_tar_text_content};
pub(crate) use zip::{extract_zip_file_bytes, extract_zip_metadata, extract_zip_text_content};

/// Archive metadata extracted from an archive file.
#[cfg_attr(alef, alef(skip))]
#[derive(Debug, Clone)]
pub struct ArchiveMetadata {
    /// Archive format (e.g., "ZIP", "TAR")
    pub format: String,
    /// List of files in the archive
    pub file_list: Vec<ArchiveEntry>,
    /// Total number of files
    pub file_count: usize,
    /// Total uncompressed size in bytes
    pub total_size: u64,
}

/// Information about a single file in an archive.
#[cfg_attr(alef, alef(skip))]
#[derive(Debug, Clone)]
pub struct ArchiveEntry {
    /// File path within the archive, copied **verbatim** from the archive's own entry
    /// name (`zip::read::ZipFile::name()`, `tar::Entry::path()`, ...).
    ///
    /// This value is untrusted and unnormalised: a hostile or malformed archive can make
    /// it an absolute path (`/etc/passwd`), a traversing relative path
    /// (`../../etc/passwd`), or a Windows drive-letter/UNC form. Nothing in this module
    /// rejects or rewrites those forms, because nothing here writes archive contents to a
    /// real filesystem path built from this string -- it is only ever used as an opaque
    /// map key (`extract_zip_text_content`/`extract_zip_file_bytes` and their TAR
    /// equivalents) or surfaced as informational metadata.
    ///
    /// A future caller that *does* want to write an entry to disk (a CLI "extract to
    /// directory" feature, an FFI binding, ...) must never join this value onto a real
    /// path directly. Use [`ArchiveEntry::confined_path`] instead, which returns a
    /// normalised path guaranteed not to escape the archive root, or `None` when the raw
    /// name cannot be confined at all.
    pub path: String,
    /// File size in bytes
    pub size: u64,
    /// Whether this is a directory
    pub is_dir: bool,
}

impl ArchiveEntry {
    /// Returns [`Self::path`] normalised and confined to the archive root, or `None` when
    /// it cannot be confined safely.
    ///
    /// This is the safe counterpart to the raw `path` field: it rejects (via `None`)
    /// exactly the cases that make `path` unsafe to use as a filesystem write target --
    /// a `..` that pops past the root, a NUL byte, and a Windows drive letter or UNC
    /// prefix -- and otherwise returns a `/`-joined, root-relative path with `.` and
    /// empty segments removed.
    ///
    /// Backslashes are normalised to `/` first, since a hostile archive can store a
    /// backslash-separated name that native path handling would treat as a single opaque
    /// segment on Unix rather than as traversal components.
    ///
    /// This function does not decide *what* to do with an unconfined entry (skip it, warn,
    /// abort the whole archive, ...); that policy belongs to the caller that would
    /// otherwise write the entry somewhere.
    pub fn confined_path(&self) -> Option<String> {
        if self.path.contains('\0') {
            return None;
        }

        let normalized = self.path.replace('\\', "/");
        if has_drive_or_unc_prefix(&normalized) {
            return None;
        }

        let mut stack: Vec<&str> = Vec::new();
        for segment in normalized.split('/') {
            match segment {
                "" | "." => {}
                ".." => {
                    stack.pop()?;
                }
                other => stack.push(other),
            }
        }

        if stack.is_empty() {
            return None;
        }

        Some(stack.join("/"))
    }
}

/// `true` when `path` begins with a Windows drive letter (`C:`) or a UNC prefix (`//`,
/// which is what a backslash-normalised `\\server\share` becomes).
fn has_drive_or_unc_prefix(path: &str) -> bool {
    let bytes = path.as_bytes();
    path.starts_with("//") || (bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':')
}

/// Common text file extensions that should be extracted from archives.
///
/// #113: the original list only covered a handful of formats, so real
/// plain-text members (source code, config files, alternate markup/data
/// formats) were treated as binary and skipped. Widened to cover the text
/// families xberg already extracts elsewhere in the pipeline.
pub(crate) const TEXT_EXTENSIONS: &[&str] = &[
    ".txt",
    ".md",
    ".markdown",
    ".json",
    ".jsonl",
    ".ndjson",
    ".xml",
    ".html",
    ".htm",
    ".csv",
    ".tsv",
    ".log",
    ".yaml",
    ".yml",
    ".toml",
    ".ini",
    ".cfg",
    ".conf",
    ".properties",
    ".env",
    ".rst",
    ".adoc",
    ".tex",
    ".sql",
    ".rs",
    ".py",
    ".js",
    ".mjs",
    ".cjs",
    ".ts",
    ".tsx",
    ".jsx",
    ".go",
    ".java",
    ".kt",
    ".rb",
    ".php",
    ".c",
    ".h",
    ".cpp",
    ".cc",
    ".hpp",
    ".cs",
    ".swift",
    ".sh",
    ".bash",
    ".zsh",
    ".ps1",
    ".css",
    ".scss",
    ".less",
    ".svg",
    ".gitignore",
];

/// Decode an archive text member's bytes to a string, detecting the charset
/// instead of dropping non-UTF-8 members. Warns when the bytes weren't clean
/// UTF-8 so a mojibake member is at least visible (xberg-io/xberg#1223).
///
/// `decode_with_provenance` (#395) reports whether the decode actually lost data --
/// via `replaced_characters` -- at the point the decision is made, which is folded
/// into this same warning rather than emitted separately: scanning the returned
/// `String` for U+FFFD afterwards would be blind to that under the `quality`
/// feature, whose mojibake cleanup strips replacement characters before this
/// function's caller ever sees the text.
pub(crate) fn decode_archive_text(bytes: &[u8], member: &str) -> String {
    match std::str::from_utf8(bytes) {
        Ok(s) => crate::utils::strip_bom(s).to_string(),
        Err(_) => {
            let outcome = crate::utils::decode_with_provenance(bytes, None);
            tracing::warn!(
                member = %member,
                replaced_characters = outcome.replaced_characters,
                "archive member is not valid UTF-8; decoding with charset detection"
            );
            crate::utils::strip_bom(&outcome.text).to_string()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::extractors::security::SecurityLimits;
    use ::tar::Builder as TarBuilder;
    use ::zip::write::{FileOptions, ZipWriter};
    use std::io::{Cursor, Write};

    fn default_limits() -> SecurityLimits {
        SecurityLimits::default()
    }

    fn entry(path: &str) -> ArchiveEntry {
        ArchiveEntry {
            path: path.to_string(),
            size: 0,
            is_dir: false,
        }
    }

    #[test]
    fn test_confined_path_returns_normalised_relative_path_unchanged() {
        assert_eq!(
            entry("folder/document.pdf").confined_path(),
            Some("folder/document.pdf".to_string())
        );
    }

    #[test]
    fn test_confined_path_rejects_traversal_past_the_root() {
        assert_eq!(entry("../../etc/passwd").confined_path(), None);
    }

    #[test]
    fn test_confined_path_allows_in_bounds_traversal_that_stays_inside_root() {
        // `a/../b` never leaves the root it started in, so it is confined even though it
        // contains a `..` component.
        assert_eq!(entry("a/../b/file.txt").confined_path(), Some("b/file.txt".to_string()));
    }

    #[test]
    fn test_confined_path_strips_leading_slash_from_absolute_unix_path() {
        // A leading `/` is treated the same as any other empty segment here (unlike the
        // OOXML/EPUB `resolve_container_entry`, an archive entry has no "container root"
        // concept to redirect to): stripping it still leaves a normal relative path.
        assert_eq!(
            entry("/tmp/malicious.txt").confined_path(),
            Some("tmp/malicious.txt".to_string())
        );
    }

    #[test]
    fn test_confined_path_rejects_windows_drive_prefix() {
        assert_eq!(entry("C:/Windows/System32/evil.dll").confined_path(), None);
    }

    #[test]
    fn test_confined_path_rejects_unc_prefix() {
        assert_eq!(entry("\\\\server\\share\\evil.dll").confined_path(), None);
    }

    #[test]
    fn test_confined_path_rejects_nul_byte() {
        assert_eq!(entry("evil\0.txt").confined_path(), None);
    }

    #[test]
    fn test_confined_path_rejects_bare_parent_dir() {
        assert_eq!(entry("..").confined_path(), None);
    }

    #[test]
    fn test_confined_path_rejects_path_made_only_of_dot_segments() {
        assert_eq!(entry("./.").confined_path(), None);
    }

    /// Regression for #113: real text members with extensions absent from the
    /// original narrow allowlist (source code, `.ini`/`.env` config, `.yml`,
    /// `.rst`) must be extracted, not skipped as binary.
    #[test]
    fn test_extract_zip_text_content_includes_widened_extensions() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.start_file("main.rs", options).unwrap();
            zip.write_all(b"fn main() {}").unwrap();

            zip.start_file("settings.ini", options).unwrap();
            zip.write_all(b"[core]\nkey=value").unwrap();

            zip.start_file(".env", options).unwrap();
            zip.write_all(b"API_KEY=secret").unwrap();

            zip.start_file("pipeline.yml", options).unwrap();
            zip.write_all(b"steps: []").unwrap();

            zip.start_file("notes.rst", options).unwrap();
            zip.write_all(b"Title\n=====").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(
            contents.len(),
            5,
            "all five widened-extension members should be extracted: {contents:?}"
        );
        assert_eq!(contents.get("main.rs").unwrap(), "fn main() {}");
        assert_eq!(contents.get("settings.ini").unwrap(), "[core]\nkey=value");
        assert_eq!(contents.get(".env").unwrap(), "API_KEY=secret");
        assert_eq!(contents.get("pipeline.yml").unwrap(), "steps: []");
        assert_eq!(contents.get("notes.rst").unwrap(), "Title\n=====");
    }

    #[test]
    fn test_extract_zip_metadata() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.start_file("test.txt", options).unwrap();
            zip.write_all(b"Hello, World!").unwrap();

            zip.start_file("dir/file.md", options).unwrap();
            zip.write_all(b"# Header").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "ZIP");
        assert_eq!(metadata.file_count, 2);
        assert_eq!(metadata.file_list.len(), 2);
        assert!(metadata.total_size > 0);
    }

    #[test]
    fn test_extract_tar_metadata() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);

            let data1 = b"Hello, World!";
            let mut header1 = ::tar::Header::new_gnu();
            header1.set_path("test.txt").unwrap();
            header1.set_size(data1.len() as u64);
            header1.set_cksum();
            tar.append(&header1, &data1[..]).unwrap();

            let data2 = b"# Header";
            let mut header2 = ::tar::Header::new_gnu();
            header2.set_path("dir/file.md").unwrap();
            header2.set_size(data2.len() as u64);
            header2.set_cksum();
            tar.append(&header2, &data2[..]).unwrap();

            tar.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_tar_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "TAR");
        assert_eq!(metadata.file_count, 2);
        assert_eq!(metadata.file_list.len(), 2);
        assert!(metadata.total_size > 0);
    }

    #[test]
    fn test_extract_zip_text_content() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.start_file("test.txt", options).unwrap();
            zip.write_all(b"Hello, World!").unwrap();

            zip.start_file("readme.md", options).unwrap();
            zip.write_all(b"# README").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(contents.len(), 2);
        assert_eq!(contents.get("test.txt").unwrap(), "Hello, World!");
        assert_eq!(contents.get("readme.md").unwrap(), "# README");
    }

    /// Regression for #1223: a non-UTF-8 (Latin-1) text member must be recovered,
    /// not silently dropped by a failed read_to_string.
    #[test]
    fn non_utf8_zip_member_is_recovered_not_dropped() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();
            zip.start_file("latin1.txt", options).unwrap();
            zip.write_all(b"caf\xe9").unwrap();
            zip.finish().unwrap();
        }
        let bytes = cursor.into_inner();
        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();
        assert!(
            contents.contains_key("latin1.txt"),
            "non-UTF-8 member must not be dropped"
        );
        let text = contents.get("latin1.txt").unwrap();
        assert!(!text.is_empty(), "recovered content must be non-empty: {text:?}");
    }

    #[test]
    fn test_extract_tar_text_content() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);

            let data1 = b"Hello, World!";
            let mut header1 = ::tar::Header::new_gnu();
            header1.set_path("test.txt").unwrap();
            header1.set_size(data1.len() as u64);
            header1.set_cksum();
            tar.append(&header1, &data1[..]).unwrap();

            let data2 = b"# README";
            let mut header2 = ::tar::Header::new_gnu();
            header2.set_path("readme.md").unwrap();
            header2.set_size(data2.len() as u64);
            header2.set_cksum();
            tar.append(&header2, &data2[..]).unwrap();

            tar.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let contents = extract_tar_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(contents.len(), 2);
        assert_eq!(contents.get("test.txt").unwrap(), "Hello, World!");
        assert_eq!(contents.get("readme.md").unwrap(), "# README");
    }

    #[test]
    fn test_extract_zip_metadata_invalid() {
        let invalid_bytes = vec![0, 1, 2, 3, 4, 5];
        let result = extract_zip_metadata(&invalid_bytes, &default_limits());
        assert!(result.is_err());
    }

    #[test]
    fn test_extract_tar_metadata_invalid() {
        let invalid_bytes = vec![0, 1, 2, 3, 4, 5];
        let result = extract_tar_metadata(&invalid_bytes, &default_limits());
        assert!(result.is_err());
    }

    #[test]
    fn test_extract_zip_metadata_with_directories() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.add_directory("dir1/", options).unwrap();
            zip.add_directory("dir1/subdir/", options).unwrap();

            zip.start_file("dir1/file1.txt", options).unwrap();
            zip.write_all(b"content1").unwrap();

            zip.start_file("dir1/subdir/file2.txt", options).unwrap();
            zip.write_all(b"content2").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "ZIP");
        assert_eq!(metadata.file_count, 4);
        assert_eq!(metadata.total_size, 16);

        let dir_count = metadata.file_list.iter().filter(|e| e.is_dir).count();
        assert_eq!(dir_count, 2);
    }

    #[test]
    fn test_extract_tar_metadata_with_directories() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);

            let mut header_dir = ::tar::Header::new_gnu();
            header_dir.set_path("dir1/").unwrap();
            header_dir.set_size(0);
            header_dir.set_entry_type(::tar::EntryType::Directory);
            header_dir.set_cksum();
            tar.append(&header_dir, &[][..]).unwrap();

            let data = b"content1";
            let mut header1 = ::tar::Header::new_gnu();
            header1.set_path("dir1/file1.txt").unwrap();
            header1.set_size(data.len() as u64);
            header1.set_cksum();
            tar.append(&header1, &data[..]).unwrap();

            tar.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_tar_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "TAR");
        assert_eq!(metadata.file_count, 2);

        let dir_count = metadata.file_list.iter().filter(|e| e.is_dir).count();
        assert_eq!(dir_count, 1);
    }

    #[test]
    fn test_extract_tar_gz_metadata() {
        let mut tar_data = Vec::new();
        {
            let mut tar = TarBuilder::new(&mut tar_data);

            let data = b"Hello from gzip!";
            let mut header = ::tar::Header::new_gnu();
            header.set_path("test.txt").unwrap();
            header.set_size(data.len() as u64);
            header.set_cksum();
            tar.append(&header, &data[..]).unwrap();

            tar.finish().unwrap();
        }

        let metadata = extract_tar_metadata(&tar_data, &default_limits()).unwrap();
        assert_eq!(metadata.format, "TAR");
        assert_eq!(metadata.file_count, 1);
        assert_eq!(metadata.file_list[0].path, "test.txt");
    }

    #[test]
    fn test_extract_7z_metadata_with_files() {
        use sevenz_rust2::{ArchiveEntry as SevenzEntry, ArchiveWriter};

        let cursor = {
            let cursor = Cursor::new(Vec::new());
            let mut sz = ArchiveWriter::new(cursor).unwrap();

            sz.push_archive_entry(
                SevenzEntry::new_file("test.txt"),
                Some(Cursor::new(b"Hello 7z!".to_vec())),
            )
            .unwrap();

            sz.push_archive_entry(
                SevenzEntry::new_file("data.json"),
                Some(Cursor::new(b"{\"key\":\"value\"}".to_vec())),
            )
            .unwrap();

            sz.finish().unwrap()
        };

        let bytes = cursor.into_inner();
        let metadata = extract_7z_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "7Z");
        assert_eq!(metadata.file_count, 2);
        assert!(metadata.total_size > 0);
    }

    #[test]
    fn test_extract_zip_within_zip() {
        let mut inner_cursor = Cursor::new(Vec::new());
        {
            let mut inner_zip = ZipWriter::new(&mut inner_cursor);
            let options = FileOptions::<'_, ()>::default();

            inner_zip.start_file("inner.txt", options).unwrap();
            inner_zip.write_all(b"Nested content").unwrap();

            inner_zip.finish().unwrap();
        }
        let inner_bytes = inner_cursor.into_inner();

        let mut outer_cursor = Cursor::new(Vec::new());
        {
            let mut outer_zip = ZipWriter::new(&mut outer_cursor);
            let options = FileOptions::<'_, ()>::default();

            outer_zip.start_file("archive.zip", options).unwrap();
            outer_zip.write_all(&inner_bytes).unwrap();

            outer_zip.start_file("readme.txt", options).unwrap();
            outer_zip.write_all(b"Outer content").unwrap();

            outer_zip.finish().unwrap();
        }

        let outer_bytes = outer_cursor.into_inner();
        let metadata = extract_zip_metadata(&outer_bytes, &default_limits()).unwrap();

        assert_eq!(metadata.file_count, 2);

        let archive_entry = metadata.file_list.iter().find(|e| e.path == "archive.zip");
        assert!(archive_entry.is_some());
        assert!(archive_entry.unwrap().size > 0);
    }

    #[test]
    fn test_extract_tar_within_tar() {
        let mut inner_cursor = Cursor::new(Vec::new());
        {
            let mut inner_tar = TarBuilder::new(&mut inner_cursor);

            let data = b"Nested content";
            let mut header = ::tar::Header::new_gnu();
            header.set_path("inner.txt").unwrap();
            header.set_size(data.len() as u64);
            header.set_cksum();
            inner_tar.append(&header, &data[..]).unwrap();

            inner_tar.finish().unwrap();
        }
        let inner_bytes = inner_cursor.into_inner();

        let mut outer_cursor = Cursor::new(Vec::new());
        {
            let mut outer_tar = TarBuilder::new(&mut outer_cursor);

            let mut header1 = ::tar::Header::new_gnu();
            header1.set_path("archive.tar").unwrap();
            header1.set_size(inner_bytes.len() as u64);
            header1.set_cksum();
            outer_tar.append(&header1, &inner_bytes[..]).unwrap();

            let data = b"Outer content";
            let mut header2 = ::tar::Header::new_gnu();
            header2.set_path("readme.txt").unwrap();
            header2.set_size(data.len() as u64);
            header2.set_cksum();
            outer_tar.append(&header2, &data[..]).unwrap();

            outer_tar.finish().unwrap();
        }

        let outer_bytes = outer_cursor.into_inner();
        let metadata = extract_tar_metadata(&outer_bytes, &default_limits()).unwrap();

        assert_eq!(metadata.file_count, 2);

        let archive_entry = metadata.file_list.iter().find(|e| e.path == "archive.tar");
        assert!(archive_entry.is_some());
    }

    #[test]
    fn test_extract_zip_corrupted_data() {
        use crate::error::XbergError;

        let mut valid_cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut valid_cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.start_file("test.txt", options).unwrap();
            zip.write_all(b"content").unwrap();

            zip.finish().unwrap();
        }

        let mut corrupted = valid_cursor.into_inner();
        corrupted.truncate(corrupted.len() / 2);

        let result = extract_zip_metadata(&corrupted, &default_limits());
        assert!(result.is_err());

        if let Err(e) = result {
            assert!(matches!(e, XbergError::Parsing { .. }));
        }
    }

    #[test]
    fn test_extract_tar_corrupted_data() {
        let mut valid_cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut valid_cursor);

            let data = b"content";
            let mut header = ::tar::Header::new_gnu();
            header.set_path("test.txt").unwrap();
            header.set_size(data.len() as u64);
            header.set_cksum();
            tar.append(&header, &data[..]).unwrap();

            tar.finish().unwrap();
        }

        let mut corrupted = valid_cursor.into_inner();
        corrupted[100] = 0xFF;

        let result = extract_tar_metadata(&corrupted, &default_limits());
        assert!(result.is_err());
    }

    #[test]
    fn test_extract_zip_empty_archive() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let zip = ZipWriter::new(&mut cursor);
            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "ZIP");
        assert_eq!(metadata.file_count, 0);
        assert_eq!(metadata.total_size, 0);
        assert_eq!(metadata.file_list.len(), 0);
    }

    #[test]
    fn test_extract_tar_empty_archive() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);
            tar.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_tar_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "TAR");
        assert_eq!(metadata.file_count, 0);
        assert_eq!(metadata.total_size, 0);
        assert_eq!(metadata.file_list.len(), 0);
    }

    #[test]
    fn test_extract_zip_multiple_text_files() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.start_file("file1.txt", options).unwrap();
            zip.write_all(b"Content 1").unwrap();

            zip.start_file("file2.md", options).unwrap();
            zip.write_all(b"# Markdown").unwrap();

            zip.start_file("data.json", options).unwrap();
            zip.write_all(b"{\"key\":\"value\"}").unwrap();

            zip.start_file("binary.bin", options).unwrap();
            zip.write_all(&[0xFF, 0xFE, 0xFD]).unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(contents.len(), 3);
        assert_eq!(contents.get("file1.txt").unwrap(), "Content 1");
        assert_eq!(contents.get("file2.md").unwrap(), "# Markdown");
        assert_eq!(contents.get("data.json").unwrap(), "{\"key\":\"value\"}");
        assert!(!contents.contains_key("binary.bin"));
    }

    #[test]
    fn test_extract_tar_multiple_text_files() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);

            let files = vec![
                ("file1.txt", b"Content 1" as &[u8]),
                ("file2.md", b"# Markdown"),
                ("data.xml", b"<root>data</root>"),
                ("config.yaml", b"key: value"),
            ];

            for (path, data) in files {
                let mut header = ::tar::Header::new_gnu();
                header.set_path(path).unwrap();
                header.set_size(data.len() as u64);
                header.set_cksum();
                tar.append(&header, data).unwrap();
            }

            tar.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let contents = extract_tar_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(contents.len(), 4);
        assert_eq!(contents.get("file1.txt").unwrap(), "Content 1");
        assert_eq!(contents.get("file2.md").unwrap(), "# Markdown");
        assert_eq!(contents.get("data.xml").unwrap(), "<root>data</root>");
        assert_eq!(contents.get("config.yaml").unwrap(), "key: value");
    }

    #[test]
    fn test_extract_zip_preserves_directory_structure() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.add_directory("root/", options).unwrap();
            zip.add_directory("root/sub1/", options).unwrap();
            zip.add_directory("root/sub2/", options).unwrap();

            zip.start_file("root/file1.txt", options).unwrap();
            zip.write_all(b"Root file").unwrap();

            zip.start_file("root/sub1/file2.txt", options).unwrap();
            zip.write_all(b"Sub1 file").unwrap();

            zip.start_file("root/sub2/file3.txt", options).unwrap();
            zip.write_all(b"Sub2 file").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        let paths: Vec<&str> = metadata.file_list.iter().map(|e| e.path.as_str()).collect();
        assert!(paths.contains(&"root/"));
        assert!(paths.contains(&"root/sub1/"));
        assert!(paths.contains(&"root/sub2/"));
        assert!(paths.contains(&"root/file1.txt"));
        assert!(paths.contains(&"root/sub1/file2.txt"));
        assert!(paths.contains(&"root/sub2/file3.txt"));
    }

    #[test]
    fn test_extract_zip_with_large_file() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            let large_content = "x".repeat(10_000);

            zip.start_file("large.txt", options).unwrap();
            zip.write_all(large_content.as_bytes()).unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.file_count, 1);
        assert_eq!(metadata.total_size, 10_000);

        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();
        assert_eq!(contents.get("large.txt").unwrap().len(), 10_000);
    }

    #[test]
    fn test_extract_zip_with_many_files() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            for i in 0..100 {
                let filename = format!("file_{}.txt", i);
                let content = format!("Content {}", i);

                zip.start_file(&filename, options).unwrap();
                zip.write_all(content.as_bytes()).unwrap();
            }

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.file_count, 100);
        assert_eq!(metadata.file_list.len(), 100);

        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();
        assert_eq!(contents.len(), 100);
    }

    #[test]
    fn test_extract_zip_with_long_paths() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            let long_path = format!("{}/file.txt", "a".repeat(200));

            zip.start_file(&long_path, options).unwrap();
            zip.write_all(b"Deep file").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_zip_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.file_count, 1);
        assert!(metadata.file_list[0].path.len() > 200);

        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();
        assert_eq!(contents.len(), 1);
    }

    #[test]
    fn test_extract_7z_text_content() {
        use sevenz_rust2::{ArchiveEntry as SevenzEntry, ArchiveWriter};

        let cursor = {
            let cursor = Cursor::new(Vec::new());
            let mut sz = ArchiveWriter::new(cursor).unwrap();

            sz.push_archive_entry(
                SevenzEntry::new_file("test.txt"),
                Some(Cursor::new(b"Hello 7z text!".to_vec())),
            )
            .unwrap();

            sz.push_archive_entry(
                SevenzEntry::new_file("readme.md"),
                Some(Cursor::new(b"# 7z README".to_vec())),
            )
            .unwrap();

            sz.finish().unwrap()
        };

        let bytes = cursor.into_inner();
        let contents = extract_7z_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(contents.len(), 2);
        assert_eq!(contents.get("test.txt").unwrap(), "Hello 7z text!");
        assert_eq!(contents.get("readme.md").unwrap(), "# 7z README");
    }

    #[test]
    fn test_extract_7z_empty_archive() {
        use sevenz_rust2::ArchiveWriter;

        let cursor = {
            let cursor = Cursor::new(Vec::new());
            let sz = ArchiveWriter::new(cursor).unwrap();
            sz.finish().unwrap()
        };

        let bytes = cursor.into_inner();
        let metadata = extract_7z_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.format, "7Z");
        assert_eq!(metadata.file_count, 0);
        assert_eq!(metadata.total_size, 0);
    }

    #[test]
    fn test_extract_tar_with_large_file() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);

            let large_content = "y".repeat(50_000);

            let mut header = ::tar::Header::new_gnu();
            header.set_path("large.txt").unwrap();
            header.set_size(large_content.len() as u64);
            header.set_cksum();
            tar.append(&header, large_content.as_bytes()).unwrap();

            tar.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let metadata = extract_tar_metadata(&bytes, &default_limits()).unwrap();

        assert_eq!(metadata.file_count, 1);
        assert_eq!(metadata.total_size, 50_000);

        let contents = extract_tar_text_content(&bytes, &default_limits()).unwrap();
        assert_eq!(contents.get("large.txt").unwrap().len(), 50_000);
    }

    #[test]
    fn test_extract_zip_text_content_filters_non_text_extensions() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            zip.start_file("document.txt", options).unwrap();
            zip.write_all(b"Text file").unwrap();

            zip.start_file("image.png", options).unwrap();
            zip.write_all(&[0x89, 0x50, 0x4E, 0x47]).unwrap();

            zip.start_file("binary.exe", options).unwrap();
            zip.write_all(&[0x4D, 0x5A]).unwrap();

            zip.start_file("config.toml", options).unwrap();
            zip.write_all(b"[section]").unwrap();

            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let contents = extract_zip_text_content(&bytes, &default_limits()).unwrap();

        assert_eq!(contents.len(), 2);
        assert!(contents.contains_key("document.txt"));
        assert!(contents.contains_key("config.toml"));
        assert!(!contents.contains_key("image.png"));
        assert!(!contents.contains_key("binary.exe"));
    }

    #[test]
    fn test_extract_7z_corrupted_data() {
        use crate::error::XbergError;

        let invalid_7z_data = vec![0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00];

        let result = extract_7z_metadata(&invalid_7z_data, &default_limits());
        assert!(result.is_err());

        if let Err(e) = result {
            assert!(matches!(e, XbergError::Parsing { .. }));
        }
    }

    #[test]
    fn test_extract_gzip_metadata() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(b"Hello from gzip!").unwrap();
        let compressed = encoder.finish().unwrap();

        let metadata = extract_gzip_metadata(&compressed, &default_limits()).unwrap();
        assert_eq!(metadata.format, "GZIP");
        assert_eq!(metadata.file_count, 1);
        assert_eq!(metadata.total_size, 16);
    }

    #[test]
    fn test_extract_gzip_text_content() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(b"Hello from gzip!").unwrap();
        let compressed = encoder.finish().unwrap();

        let contents = extract_gzip_text_content(&compressed, &default_limits()).unwrap();
        assert_eq!(contents.len(), 1);
        assert!(contents.values().next().unwrap().contains("Hello from gzip!"));
    }

    #[test]
    fn test_decompress_gzip() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(b"test content").unwrap();
        let compressed = encoder.finish().unwrap();

        let decompressed = decompress_gzip(&compressed, &default_limits()).unwrap();
        assert_eq!(String::from_utf8(decompressed).unwrap(), "test content");
    }

    #[test]
    fn test_extract_gzip_invalid_data() {
        let invalid = vec![0, 1, 2, 3, 4, 5];
        let result = extract_gzip_metadata(&invalid, &default_limits());
        assert!(result.is_err());
    }

    #[test]
    fn test_extract_gzip_empty_content() {
        use flate2::Compression;
        use flate2::write::GzEncoder;

        let encoder = GzEncoder::new(Vec::new(), Compression::default());
        let compressed = encoder.finish().unwrap();

        let metadata = extract_gzip_metadata(&compressed, &default_limits()).unwrap();
        assert_eq!(metadata.format, "GZIP");
        assert_eq!(metadata.total_size, 0);
    }

    #[test]
    fn test_zip_too_many_files_rejected() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();

            for i in 0..5 {
                let filename = format!("file_{}.txt", i);
                zip.start_file(&filename, options).unwrap();
                zip.write_all(b"content").unwrap();
            }
            zip.finish().unwrap();
        }

        let bytes = cursor.into_inner();
        let limits = SecurityLimits {
            max_files_in_archive: 3,
            ..SecurityLimits::default()
        };
        let result = extract_zip_metadata(&bytes, &limits);
        assert!(result.is_err());
    }

    #[test]
    fn test_gzip_bomb_rejected() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(&[b'A'; 1024]).unwrap();
        let compressed = encoder.finish().unwrap();

        let limits = SecurityLimits {
            max_archive_size: 100,
            ..SecurityLimits::default()
        };
        let result = extract_gzip_metadata(&compressed, &limits);
        assert!(result.is_err());
    }

    #[test]
    fn test_extract_gzip_compressed_tar_metadata() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut tar_data = Vec::new();
        {
            let mut tar = TarBuilder::new(&mut tar_data);

            let data1 = b"Hello from tar.gz!";
            let mut header1 = ::tar::Header::new_gnu();
            header1.set_path("test.txt").unwrap();
            header1.set_size(data1.len() as u64);
            header1.set_cksum();
            tar.append(&header1, &data1[..]).unwrap();

            let data2 = b"# Markdown file";
            let mut header2 = ::tar::Header::new_gnu();
            header2.set_path("readme.md").unwrap();
            header2.set_size(data2.len() as u64);
            header2.set_cksum();
            tar.append(&header2, &data2[..]).unwrap();

            tar.finish().unwrap();
        }

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(&tar_data).unwrap();
        let gzip_compressed = encoder.finish().unwrap();

        let metadata = extract_gzip_metadata(&gzip_compressed, &default_limits()).unwrap();

        assert_eq!(metadata.format, "GZIP+TAR");
        assert_eq!(metadata.file_count, 2);
        assert_eq!(metadata.file_list.len(), 2);
        assert!(metadata.total_size > 0);

        let paths: Vec<&str> = metadata.file_list.iter().map(|e| e.path.as_str()).collect();
        assert!(paths.contains(&"test.txt"));
        assert!(paths.contains(&"readme.md"));
    }

    #[test]
    fn test_extract_gzip_compressed_tar_text_content() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut tar_data = Vec::new();
        {
            let mut tar = TarBuilder::new(&mut tar_data);

            let data1 = b"Hello from tar.gz!";
            let mut header1 = ::tar::Header::new_gnu();
            header1.set_path("test.txt").unwrap();
            header1.set_size(data1.len() as u64);
            header1.set_cksum();
            tar.append(&header1, &data1[..]).unwrap();

            let data2 = b"# Markdown content";
            let mut header2 = ::tar::Header::new_gnu();
            header2.set_path("readme.md").unwrap();
            header2.set_size(data2.len() as u64);
            header2.set_cksum();
            tar.append(&header2, &data2[..]).unwrap();

            tar.finish().unwrap();
        }

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(&tar_data).unwrap();
        let gzip_compressed = encoder.finish().unwrap();

        let contents = extract_gzip_text_content(&gzip_compressed, &default_limits()).unwrap();

        assert_eq!(contents.len(), 2);
        assert_eq!(contents.get("test.txt").unwrap(), "Hello from tar.gz!");
        assert_eq!(contents.get("readme.md").unwrap(), "# Markdown content");
    }

    #[test]
    fn test_extract_gzip_compressed_tar_both() {
        use flate2::Compression;
        use flate2::write::GzEncoder;
        use std::io::Write;

        let mut tar_data = Vec::new();
        {
            let mut tar = TarBuilder::new(&mut tar_data);

            let data = b"Combined test content";
            let mut header = ::tar::Header::new_gnu();
            header.set_path("combined.txt").unwrap();
            header.set_size(data.len() as u64);
            header.set_cksum();
            tar.append(&header, &data[..]).unwrap();

            tar.finish().unwrap();
        }

        let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
        encoder.write_all(&tar_data).unwrap();
        let gzip_compressed = encoder.finish().unwrap();

        let (metadata, contents) = extract_gzip(&gzip_compressed, &default_limits()).unwrap();

        assert_eq!(metadata.format, "GZIP+TAR");
        assert_eq!(metadata.file_count, 1);
        assert_eq!(contents.get("combined.txt").unwrap(), "Combined test content");
    }

    /// A tracing `Layer` that records the `replaced_characters` field of every emitted
    /// event, keyed by whether the field was present at all.
    #[derive(Clone, Default)]
    struct ReplacedCharactersCapture {
        events: std::sync::Arc<std::sync::Mutex<Vec<Option<bool>>>>,
    }

    impl<S> tracing_subscriber::Layer<S> for ReplacedCharactersCapture
    where
        S: tracing::Subscriber,
    {
        fn on_event(&self, event: &tracing::Event<'_>, _ctx: tracing_subscriber::layer::Context<'_, S>) {
            struct Visitor(Option<bool>);
            impl tracing::field::Visit for Visitor {
                fn record_debug(&mut self, _field: &tracing::field::Field, _value: &dyn std::fmt::Debug) {}
                fn record_bool(&mut self, field: &tracing::field::Field, value: bool) {
                    if field.name() == "replaced_characters" {
                        self.0 = Some(value);
                    }
                }
            }
            let mut visitor = Visitor(None);
            event.record(&mut visitor);
            self.events.lock().unwrap().push(visitor.0);
        }
    }

    /// #395: `decode_with_provenance` reports data loss at the point the decode
    /// actually happens, so it must survive into the existing lossy-decode warning
    /// as a `replaced_characters` field instead of being discarded the way
    /// `safe_decode` discarded it.
    ///
    /// Deliberately not run under `quality`: there chardetng resolves arbitrary
    /// bytes to a single-byte encoding that maps all of 0x00-0xFF, so nothing is
    /// *replaced* -- see the identical note on
    /// `extractors::text::should_warn_when_text_source_is_not_valid_utf8`.
    #[cfg(not(feature = "quality"))]
    #[test]
    fn decode_archive_text_reports_replaced_characters_true_for_invalid_utf8() {
        use tracing_subscriber::layer::SubscriberExt as _;

        let capture = ReplacedCharactersCapture::default();
        let filter = tracing_subscriber::EnvFilter::new("warn");
        let subscriber = tracing_subscriber::registry().with(filter).with(capture.clone());

        let bytes: &[u8] = &[b'A', 0xFF, 0xFE, b'B'];
        let text = tracing::subscriber::with_default(subscriber, || decode_archive_text(bytes, "bad.txt"));

        assert!(!text.is_empty(), "decode must still return text, got {text:?}");
        let events = capture.events.lock().unwrap();
        assert_eq!(events.len(), 1, "expected exactly one warning event, got {events:?}");
        assert_eq!(
            events[0],
            Some(true),
            "expected a replaced_characters=true field on the warning, got {:?}",
            events[0]
        );
    }

    /// Covers per-member rejection and error naming for an oversized ZIP member's text
    /// content -- NOT memory-boundedness. `extract_zip_text_content` takes `bytes: &[u8]` and
    /// builds a concrete `zip::read::ZipFile` reader internally; there is no seam here to
    /// substitute a counting/instrumented `Read` for it, so the actual property the `.take()`
    /// exists for (the reader is never asked for more than `cap + 1` bytes) cannot be observed
    /// from this test. It is covered by inspection only. What this test does prove: a member
    /// whose decompressed content dwarfs `max_content_size` is rejected by a *member-scoped*
    /// error that names the member, rather than surfacing only from the aggregate
    /// `total_content_size` check (which, once several members have been summed, can no longer
    /// report which one was responsible).
    ///
    /// Neutralisation that must break this test: replace `.take(cap.saturating_add(1))` with
    /// `.take(u64::MAX)` in `extract_zip_text_content`. That neutralisation does NOT break this test on its
    /// own -- the post-read `raw.len() as u64 > cap` check a few lines below still fires and
    /// still names "huge.txt", so the assertion still passes. Only removing that length check
    /// too (or renaming the error's member field) would fail it, which is exactly the point:
    /// this test cannot distinguish a bounded reader from an unbounded one.
    /// `--features archives`.
    #[test]
    fn test_zip_text_content_names_offending_member_when_it_exceeds_content_cap() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();
            zip.start_file("huge.txt", options).unwrap();
            // Highly compressible filler large enough to dwarf a tiny cap without needing a
            // gigabyte-scale allocation in the test process.
            zip.write_all(&vec![b'A'; 200_000]).unwrap();
            zip.finish().unwrap();
        }
        let bytes = cursor.into_inner();
        let limits = SecurityLimits {
            max_content_size: 1_000,
            ..SecurityLimits::default()
        };

        let result = extract_zip_text_content(&bytes, &limits);

        let error = result.expect_err("a member whose decompressed size dwarfs max_content_size must be rejected");
        let message = error.to_string();
        assert!(
            message.contains("huge.txt"),
            "the rejection must name the offending member instead of only reporting an \
             aggregate total that cannot identify one: {message}"
        );
    }

    /// Same defect family, different call: `extract_zip_file_bytes`. See the doc comment on
    /// `test_zip_text_content_names_offending_member_when_it_exceeds_content_cap` for why this
    /// covers per-member rejection and naming, not memory-boundedness.
    ///
    /// Neutralisation that must break this test: replace `.take(cap.saturating_add(1))` with
    /// `.take(u64::MAX)` in `extract_zip_file_bytes` -- and, as above, that alone does not break it, since the
    /// `content.len() as u64 > cap` check still fires and still names "huge.bin".
    /// `--features archives`.
    #[test]
    fn test_zip_file_bytes_names_offending_member_when_it_exceeds_archive_cap() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut zip = ZipWriter::new(&mut cursor);
            let options = FileOptions::<'_, ()>::default();
            zip.start_file("huge.bin", options).unwrap();
            zip.write_all(&vec![0xABu8; 200_000]).unwrap();
            zip.finish().unwrap();
        }
        let bytes = cursor.into_inner();
        let limits = SecurityLimits {
            max_archive_size: 1_000,
            ..SecurityLimits::default()
        };

        let result = extract_zip_file_bytes(&bytes, &limits);

        let error = result.expect_err("a member whose decompressed size dwarfs max_archive_size must be rejected");
        let message = error.to_string();
        assert!(
            message.contains("huge.bin"),
            "the rejection must name the offending member instead of only reporting an \
             aggregate total that cannot identify one: {message}"
        );
    }

    /// TAR analogue of `test_zip_text_content_names_offending_member_when_it_exceeds_content_cap`.
    /// Same limitation applies: `extract_tar_text_content` takes `bytes: &[u8]` and builds a
    /// concrete `tar::Entry` reader internally, with no seam to inject a counting `Read`, so
    /// this covers per-member rejection and error naming only -- memory-boundedness rests on
    /// inspection.
    ///
    /// Neutralisation that must break this test: replace `.take(cap.saturating_add(1))` with
    /// `.take(u64::MAX)` in `extract_tar_text_content`. As with ZIP, that alone does not break it: the
    /// `raw.len() as u64 > cap` check still fires and still names "huge.txt".
    /// `--features archives`.
    #[test]
    fn test_tar_text_content_names_offending_member_when_it_exceeds_content_cap() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);
            let data = vec![b'B'; 200_000];
            let mut header = ::tar::Header::new_gnu();
            header.set_path("huge.txt").unwrap();
            header.set_size(data.len() as u64);
            header.set_cksum();
            tar.append(&header, &data[..]).unwrap();
            tar.finish().unwrap();
        }
        let bytes = cursor.into_inner();
        let limits = SecurityLimits {
            max_content_size: 1_000,
            ..SecurityLimits::default()
        };

        let result = extract_tar_text_content(&bytes, &limits);

        let error = result.expect_err("a member whose declared size dwarfs max_content_size must be rejected");
        let message = error.to_string();
        assert!(
            message.contains("huge.txt"),
            "the rejection must name the offending member instead of only reporting an \
             aggregate total that cannot identify one: {message}"
        );
    }

    /// TAR analogue of `test_zip_file_bytes_names_offending_member_when_it_exceeds_archive_cap`.
    ///
    /// Neutralisation that must break this test: replace `.take(cap.saturating_add(1))` with
    /// `.take(u64::MAX)` in `extract_tar_file_bytes`. As above, that alone does not break it: the
    /// `content.len() as u64 > cap` check still fires and still names "huge.bin".
    /// `--features archives`.
    #[test]
    fn test_tar_file_bytes_names_offending_member_when_it_exceeds_archive_cap() {
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut tar = TarBuilder::new(&mut cursor);
            let data = vec![0xCDu8; 200_000];
            let mut header = ::tar::Header::new_gnu();
            header.set_path("huge.bin").unwrap();
            header.set_size(data.len() as u64);
            header.set_cksum();
            tar.append(&header, &data[..]).unwrap();
            tar.finish().unwrap();
        }
        let bytes = cursor.into_inner();
        let limits = SecurityLimits {
            max_archive_size: 1_000,
            ..SecurityLimits::default()
        };

        let result = extract_tar_file_bytes(&bytes, &limits);

        let error = result.expect_err("a member whose declared size dwarfs max_archive_size must be rejected");
        let message = error.to_string();
        assert!(
            message.contains("huge.bin"),
            "the rejection must name the offending member instead of only reporting an \
             aggregate total that cannot identify one: {message}"
        );
    }

    /// A member that is already valid UTF-8 never reaches the lossy-decode branch at
    /// all, so it must not emit any warning -- and therefore no `replaced_characters`
    /// field -- regardless of build configuration.
    #[test]
    fn decode_archive_text_emits_no_warning_for_valid_utf8() {
        use tracing_subscriber::layer::SubscriberExt as _;

        let capture = ReplacedCharactersCapture::default();
        let filter = tracing_subscriber::EnvFilter::new("warn");
        let subscriber = tracing_subscriber::registry().with(filter).with(capture.clone());

        let text = tracing::subscriber::with_default(subscriber, || {
            decode_archive_text("Hello, World!".as_bytes(), "clean.txt")
        });

        assert_eq!(text, "Hello, World!");
        assert!(
            capture.events.lock().unwrap().is_empty(),
            "valid UTF-8 must not emit a decode warning, got {:?}",
            capture.events.lock().unwrap()
        );
    }
}
