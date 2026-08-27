//! Image path resolution for markup extractors.
//!
//! Resolves relative image paths found in markup documents (Markdown, LaTeX, RST,
//! Org-mode, Typst, Djot, DocBook) to actual filesystem paths, reads the image data,
//! and attaches them to the extraction result.

use std::borrow::Cow;
use std::path::{Path, PathBuf};

use bytes::Bytes;

use crate::core::config::ExtractionConfig;
use crate::types::ExtractedImage;
use crate::types::internal::InternalDocument;
use crate::types::uri::UriKind;

/// Maximum image file size: 50 MB.
const MAX_IMAGE_SIZE: u64 = 50 * 1024 * 1024;

/// Resolve a relative image reference against a base directory.
///
/// Returns `None` for URLs, absolute paths, and paths that escape `base_dir`
/// via traversal (`..`). Returns `Some(resolved)` for safe relative paths.
///
/// This function performs no filesystem access — it only validates the
/// structural safety of the path.
pub(crate) fn resolve_image_path(base_dir: &Path, image_ref: &str) -> Option<PathBuf> {
    let trimmed = image_ref.trim();

    if trimmed.starts_with("http://")
        || trimmed.starts_with("https://")
        || trimmed.starts_with("data:")
        || trimmed.starts_with("ftp://")
        || trimmed.starts_with("mailto:")
    {
        return None;
    }

    let path_str = if let Some(stripped) = trimmed.strip_prefix("file://") {
        stripped
    } else if let Some(stripped) = trimmed.strip_prefix("file:") {
        stripped
    } else {
        trimmed
    };

    if path_str.starts_with('/')
        || (path_str.len() >= 2 && path_str.as_bytes()[0].is_ascii_alphabetic() && path_str.as_bytes()[1] == b':')
    {
        return None;
    }

    // Normalise `base_dir` first, then apply `path_str`'s components on top of it,
    // refusing to pop past the boundary between the two. Joining the two into one string,
    // normalising the result, and then prefix-checking against the normalised base (the
    // previous approach) cannot tell "popped back to exactly the base" apart from "popped
    // straight through the base and out the other side": when `base_dir` is empty (its
    // normalised form has zero components -- the case for a bare filename with no
    // directory, whose `Path::parent()` is `""`), an empty path is a prefix of every path,
    // so `Path::starts_with` passed vacuously and a pure-".." reference like `"../x"`
    // silently resolved to `"x"` instead of being rejected.
    let norm_base = normalize_path(base_dir);
    let mut components: Vec<std::path::Component<'_>> = norm_base.components().collect();
    let base_len = components.len();

    for component in Path::new(path_str).components() {
        match component {
            std::path::Component::ParentDir => {
                if components.len() <= base_len {
                    return None;
                }
                components.pop();
            }
            std::path::Component::CurDir => {}
            other => components.push(other),
        }
    }

    Some(components.iter().collect())
}

/// Read an image file and produce an `ExtractedImage`.
///
/// Returns `None` if the file does not exist, is not a regular file, exceeds the size
/// limit, has an unrecognised extension, or -- once the file is known to exist -- turns
/// out to be a symlink that resolves outside `base_dir`.
pub(crate) fn read_image_file(path: &Path, base_dir: &Path, image_index: u32) -> Option<ExtractedImage> {
    let meta = std::fs::metadata(path).ok()?;
    if !meta.is_file() {
        return None;
    }
    if meta.len() > MAX_IMAGE_SIZE {
        return None;
    }

    // `resolve_image_path` validates only the *structural* safety of the path -- it never
    // touches the filesystem, so it cannot see a symlink. Now that the file is confirmed to
    // exist, canonicalise both the resolved path and the base directory and re-check
    // containment: a symlink inside `base_dir` that points outside it (e.g. a `.png`-named
    // link to `/etc/shadow`) would otherwise be followed by `fs::read` below, since
    // `fs::metadata`/`fs::read` both follow symlinks by default.
    let canonical_path = std::fs::canonicalize(path).ok()?;
    let canonical_base = std::fs::canonicalize(base_dir).ok()?;
    if !canonical_path.starts_with(&canonical_base) {
        return None;
    }

    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .map(|s| s.to_ascii_lowercase())?;

    let format: Cow<'static, str> = match ext.as_str() {
        "png" => Cow::Borrowed("png"),
        "jpg" | "jpeg" => Cow::Borrowed("jpeg"),
        "gif" => Cow::Borrowed("gif"),
        "webp" => Cow::Borrowed("webp"),
        "svg" => Cow::Borrowed("svg"),
        "bmp" => Cow::Borrowed("bmp"),
        "tiff" | "tif" => Cow::Borrowed("tiff"),
        "avif" => Cow::Borrowed("avif"),
        _ => return None,
    };

    let data = std::fs::read(&canonical_path).ok()?;
    let source_path = path.to_string_lossy().into_owned();

    Some(ExtractedImage {
        data: Bytes::from(data),
        format,
        image_index,
        page_number: None,
        width: None,
        height: None,
        colorspace: None,
        bits_per_component: None,
        is_mask: false,
        description: None,
        ocr_result: None,
        bounding_box: None,
        source_path: Some(source_path),
        image_kind: None,
        kind_confidence: None,
        cluster_id: None,
        caption: None,
        qr_codes: None,
        data_base64: None,
    })
}

/// Resolve image URIs in an `InternalDocument` to actual image data.
///
/// Iterates over all `UriKind::Image` entries, resolves them relative to
/// `base_dir`, reads the file, and appends the result to `doc.images`.
/// No-op when image extraction is disabled in `config`.
pub(crate) fn resolve_image_uris(doc: &mut InternalDocument, base_dir: &Path, config: &ExtractionConfig) {
    if !config.needs_image_data() {
        return;
    }

    let mut image_index = doc.images.len() as u32;

    let image_uri_indices: Vec<usize> = doc
        .uris
        .iter()
        .enumerate()
        .filter(|(_, uri)| uri.kind == UriKind::Image)
        .map(|(i, _)| i)
        .collect();

    for idx in image_uri_indices {
        if let Some(resolved) = resolve_image_path(base_dir, &doc.uris[idx].url)
            && let Some(img) = read_image_file(&resolved, base_dir, image_index)
        {
            doc.images.push(img);
            image_index += 1;
        }
    }
}

/// Read a path, extract content, then resolve image URIs.
///
/// Shared helper for markup extractors (Markdown, LaTeX, RST, Org-mode, Typst,
/// Djot, DocBook, MDX) that need to resolve relative image paths after extraction.
pub(crate) async fn extract_file_with_image_resolution(
    extractor: &(dyn crate::plugins::InternalDocumentExtractor + Sync),
    path: &Path,
    mime_type: &str,
    config: &ExtractionConfig,
) -> crate::Result<InternalDocument> {
    let bytes = crate::core::io::open_file_bytes(path)?;
    let mut doc = extractor.extract_content(&bytes, mime_type, config).await?;
    if let Some(base_dir) = path.parent() {
        resolve_image_uris(&mut doc, base_dir, config);
    }
    Ok(doc)
}

/// Normalize a path by resolving `.` and `..` components without filesystem access.
fn normalize_path(path: &Path) -> PathBuf {
    let mut components = Vec::new();
    for component in path.components() {
        match component {
            std::path::Component::ParentDir => {
                components.pop();
            }
            std::path::Component::CurDir => {}
            c => components.push(c),
        }
    }
    components.iter().collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::Path;

    #[test]
    fn test_resolve_relative_path() {
        let base = Path::new("/home/user/docs");
        let result = resolve_image_path(base, "images/photo.png");
        assert_eq!(result, Some(PathBuf::from("/home/user/docs/images/photo.png")));
    }

    #[test]
    fn test_resolve_nested_relative() {
        let base = Path::new("/project/content");
        let result = resolve_image_path(base, "images/subfolder/nested.png");
        assert_eq!(
            result,
            Some(PathBuf::from("/project/content/images/subfolder/nested.png"))
        );
    }

    #[test]
    fn test_reject_absolute_path() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "/etc/passwd"), None);
    }

    #[test]
    fn test_reject_traversal() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "../../etc/passwd"), None);
    }

    #[test]
    fn test_reject_http_url() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "https://example.com/img.png"), None);
    }

    #[test]
    fn test_reject_data_uri() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "data:image/png;base64,abc"), None);
    }

    #[test]
    fn test_nonexistent_file_still_resolves() {
        let base = Path::new("/nonexistent/base");
        let result = resolve_image_path(base, "sub/image.jpg");
        assert_eq!(result, Some(PathBuf::from("/nonexistent/base/sub/image.jpg")));
    }

    #[test]
    fn test_path_with_spaces() {
        let base = Path::new("/home/user/my docs");
        let result = resolve_image_path(base, "my images/photo.png");
        assert_eq!(result, Some(PathBuf::from("/home/user/my docs/my images/photo.png")));
    }

    #[test]
    fn test_windows_backslash() {
        let base = Path::new("/home/user/docs");
        let result = resolve_image_path(base, "images/photo.png");
        assert!(result.is_some());
    }

    #[test]
    fn test_reject_ftp_url() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "ftp://server/img.png"), None);
    }

    #[test]
    fn test_reject_mailto() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "mailto:user@example.com"), None);
    }

    #[test]
    fn test_file_uri_stripped() {
        let base = Path::new("/home/user/docs");
        let result = resolve_image_path(base, "file://images/photo.png");
        assert_eq!(result, Some(PathBuf::from("/home/user/docs/images/photo.png")));
    }

    #[test]
    fn test_reject_windows_absolute() {
        let base = Path::new("/home/user/docs");
        assert_eq!(resolve_image_path(base, "C:\\Windows\\img.png"), None);
    }

    // Hardening: `base_dir` can legitimately be empty. `Path::parent()` on a bare filename
    // with no directory component (e.g. extracting from `"file.md"` in the current
    // directory) returns `Some("")`. The previous algorithm normalised the fully joined
    // path and prefix-checked it against the normalised (empty) base; an empty path is a
    // prefix of every path, so the check passed vacuously and `"../x"` silently resolved to
    // `"x"` instead of being rejected. These pin the fix; the first test fails against the
    // unfixed code (it returns `Some("etc/passwd")` there instead of `None`).

    #[test]
    fn test_empty_base_dir_rejects_pure_parent_traversal() {
        let base = Path::new("");
        assert_eq!(resolve_image_path(base, "../etc/passwd"), None);
    }

    #[test]
    fn test_empty_base_dir_still_resolves_a_benign_relative_path() {
        let base = Path::new("");
        assert_eq!(
            resolve_image_path(base, "images/photo.png"),
            Some(PathBuf::from("images/photo.png"))
        );
    }

    #[test]
    fn test_trailing_parent_resolves_back_to_the_base_directory() {
        let base = Path::new("/home/user/docs");
        assert_eq!(
            resolve_image_path(base, "sub/.."),
            Some(PathBuf::from("/home/user/docs"))
        );
    }

    // Hardening: `resolve_image_path` only validates path *structure*, never touching the
    // filesystem, so it cannot see a symlink. `read_image_file` now re-checks containment
    // via `canonicalize` once the file is confirmed to exist. Unix-only: creating a symlink
    // on Windows needs elevated privileges the CI runner may not have, and the bug being
    // fixed is not platform-specific in a way that requires a Windows-side regression here.

    #[cfg(unix)]
    #[test]
    fn test_symlink_escaping_base_dir_is_rejected() {
        use std::os::unix::fs::symlink;

        let tmp = std::env::temp_dir().join(format!("xberg_path_resolver_symlink_escape_{}", std::process::id()));
        let base_dir = tmp.join("docs");
        std::fs::create_dir_all(&base_dir).unwrap();

        let outside_target = tmp.join("secret.png");
        std::fs::write(&outside_target, b"outside").unwrap();

        let link_path = base_dir.join("photo.png");
        symlink(&outside_target, &link_path).unwrap();

        let resolved = resolve_image_path(&base_dir, "photo.png").expect("structurally in-bounds");
        assert_eq!(resolved, link_path);

        let image = read_image_file(&resolved, &base_dir, 0);
        assert!(
            image.is_none(),
            "a symlink resolving outside base_dir must be rejected once the file is known to exist"
        );

        let _ = std::fs::remove_dir_all(&tmp);
    }

    #[cfg(unix)]
    #[test]
    fn test_symlink_within_base_dir_still_resolves() {
        use std::os::unix::fs::symlink;

        let tmp = std::env::temp_dir().join(format!("xberg_path_resolver_symlink_ok_{}", std::process::id()));
        let base_dir = tmp.join("docs");
        let sub_dir = base_dir.join("assets");
        std::fs::create_dir_all(&sub_dir).unwrap();

        let real_target = sub_dir.join("real.png");
        std::fs::write(&real_target, b"inside").unwrap();

        let link_path = base_dir.join("photo.png");
        symlink(&real_target, &link_path).unwrap();

        let resolved = resolve_image_path(&base_dir, "photo.png").expect("structurally in-bounds");
        let image = read_image_file(&resolved, &base_dir, 0).expect("a symlink resolving within base_dir must succeed");
        assert_eq!(image.data.as_ref(), b"inside".as_slice());

        let _ = std::fs::remove_dir_all(&tmp);
    }
}
