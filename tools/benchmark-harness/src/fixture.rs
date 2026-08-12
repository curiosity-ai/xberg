//! Fixture loading and management
//!
//! Fixtures are JSON files that describe test documents and their metadata.
//!
//! ## Fixture Format
//!
//! ```json
//! {
//!   "document": "path/to/document.pdf",
//!   "file_type": "pdf",
//!   "file_size": 1024000,
//!   "expected_frameworks": ["xberg", "docling"],
//!   // Note: frameworks can be Xberg language bindings or open source extraction alternatives
//!   "metadata": {
//!     "title": "Test Document",
//!     "pages": 10,
//!     "requires_ocr": false  // Optional: override OCR requirement detection
//!   },
//!   "ground_truth": {
//!     "text_file": "path/to/ground_truth.txt",
//!     "source": "pdf_text_layer"
//!   }
//! }
//! ```

use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::path::{Component, Path, PathBuf};

pub(crate) fn is_split_sidecar(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| name.ends_with(".split.json"))
}

/// A fixture describing a test document
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Fixture {
    /// Path to the test document (relative to fixture file)
    pub document: PathBuf,

    /// File type (extension without dot, e.g., "pdf")
    pub file_type: String,

    /// File size in bytes
    pub file_size: u64,

    /// Extraction frameworks that should be able to process this file
    /// (can be Xberg language bindings or open source extraction alternatives)
    #[serde(default)]
    pub expected_frameworks: Vec<String>,

    /// Additional metadata about the document
    #[serde(default)]
    pub metadata: HashMap<String, serde_json::Value>,

    /// Ground truth for quality assessment (optional)
    #[serde(default)]
    pub ground_truth: Option<GroundTruth>,
}

/// Ground truth data for quality assessment
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GroundTruth {
    /// Path to ground truth text file (optional — some fixtures only have markdown GT)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text_file: Option<PathBuf>,

    /// Path to ground truth markdown file for structural quality scoring (optional)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub markdown_file: Option<PathBuf>,

    /// Path to a flat `{ "field_name": "value" }` JSON file for form-field quality scoring.
    ///
    /// Used by the `field-quality` subcommand `FormFields` mode.
    /// Path is relative to the fixture file's directory.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fields_json: Option<PathBuf>,

    /// Path to a `{ "formulas": ["latex1", ...] }` JSON file for formula quality scoring.
    ///
    /// Used by the `field-quality` subcommand `Formula` mode.
    /// Path is relative to the fixture file's directory.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub formulas_json: Option<PathBuf>,

    /// Source of the ground truth ("pdf_text_layer", "markdown_file", "manual")
    pub source: String,
}

impl Fixture {
    /// Load a fixture from a JSON file
    pub fn from_file(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        let contents = std::fs::read_to_string(path).map_err(Error::Io)?;
        let fixture: Fixture = serde_json::from_str(&contents)?;
        fixture.validate(path)?;
        Ok(fixture)
    }

    /// Validate the fixture
    ///
    /// Performs comprehensive validation including:
    /// - Path validation (relative paths only)
    /// - File type validation (non-empty)
    /// - Ground truth validation:
    ///   - Relative path requirement
    ///   - Valid source type
    ///   - File existence check (relative to fixture directory)
    fn validate(&self, fixture_path: &Path) -> Result<()> {
        Self::validate_fixture_relative_path(fixture_path, &self.document, "document")?;

        if self.file_type.is_empty() {
            return Err(Error::InvalidFixture {
                path: fixture_path.to_path_buf(),
                reason: "file_type cannot be empty".to_string(),
            });
        }

        if let Some(language) = self.metadata.get("ocr_language") {
            let Some(language) = language.as_str() else {
                return Err(Error::InvalidFixture {
                    path: fixture_path.to_path_buf(),
                    reason: "metadata.ocr_language must be a string".to_string(),
                });
            };
            let codes = crate::adapter::canonicalize_ocr_languages(language);
            if codes.is_empty()
                || codes
                    .iter()
                    .any(|code| !crate::adapter::is_valid_ocr_language_code(code))
            {
                return Err(Error::InvalidFixture {
                    path: fixture_path.to_path_buf(),
                    reason: "metadata.ocr_language must contain '+'-separated Tesseract language codes".to_string(),
                });
            }
        }

        if let Some(gt) = &self.ground_truth {
            for (field, relative_path) in [
                ("ground_truth.text_file", gt.text_file.as_deref()),
                ("ground_truth.markdown_file", gt.markdown_file.as_deref()),
                ("ground_truth.fields_json", gt.fields_json.as_deref()),
                ("ground_truth.formulas_json", gt.formulas_json.as_deref()),
            ] {
                if let Some(relative_path) = relative_path {
                    Self::validate_fixture_relative_path(fixture_path, relative_path, field)?;
                }
            }

            if !matches!(
                gt.source.as_str(),
                "pdf_text_layer"
                    | "markdown_file"
                    | "manual"
                    | "vision"
                    | "python-docx"
                    | "python-pptx"
                    | "openpyxl"
                    | "codex-vision"
                    | "raw_source"
                    | "pandoc"
                    | "python_email"
                    | "extract_msg"
                    | "nbformat"
                    | "xml_parse"
                    | "beautifulsoup"
                    | "xlrd"
                    | "antiword"
                    | "libreoffice"
                    | "odfpy"
                    | "ebooklib"
                    | "striprtf"
                    | "pyxlsb"
                    | "olefile"
                    | "omnidocbench"
                    | "mistral-pixtral"
                    | "nougat"
                    | "readoc"
                    | "parsebench"
                    | "fintabnet"
                    | "federal_register"
                    | "apple_preview"
            ) {
                return Err(Error::InvalidFixture {
                    path: fixture_path.to_path_buf(),
                    reason: format!("invalid ground_truth.source: {}", gt.source),
                });
            }

            Self::validate_ground_truth_file(fixture_path, gt.text_file.as_deref(), "text")?;
            Self::validate_ground_truth_file(fixture_path, gt.markdown_file.as_deref(), "markdown")?;
        }

        Ok(())
    }

    /// Resolve a fixture-owned relative path without allowing it to escape its trust boundary.
    ///
    /// Repository fixtures intentionally use `../../../test_documents/...`, so their boundary is
    /// the repository root. Standalone fixture trees use the fixture file's directory. Existing
    /// paths are canonicalized as well, preventing a symlink inside either boundary from escaping.
    /// ~keep
    fn validate_fixture_relative_path(fixture_path: &Path, relative_path: &Path, field: &str) -> Result<PathBuf> {
        if relative_path.is_absolute() {
            return Err(Error::InvalidFixture {
                path: fixture_path.to_path_buf(),
                reason: format!("{field} must be relative"),
            });
        }

        // A bare filename has an empty parent path, which must resolve relative to the current
        // directory just like a missing parent. Keep the canonical trust boundary check below. ~keep
        let fixture_dir = fixture_path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."));
        let canonical_fixture_dir = fixture_dir.canonicalize().map_err(|error| Error::InvalidFixture {
            path: fixture_path.to_path_buf(),
            reason: format!("unable to resolve fixture directory {}: {error}", fixture_dir.display()),
        })?;
        // Resolve the checked-out repository from the runtime fixture location. The harness
        // binary is cached and executed by separate CI jobs, so its build-time source path may
        // not exist on the runner that validates fixtures. ~keep
        let repository_root = repository_fixture_root(&canonical_fixture_dir);
        let allowed_root = repository_root.as_deref().unwrap_or(&canonical_fixture_dir);
        let resolved = normalize_path(&canonical_fixture_dir.join(relative_path));
        if !resolved.starts_with(allowed_root) {
            return Err(Error::InvalidFixture {
                path: fixture_path.to_path_buf(),
                reason: format!(
                    "{field} escapes the fixture trust boundary: {}",
                    relative_path.display()
                ),
            });
        }

        if resolved.exists() {
            let canonical = resolved.canonicalize().map_err(|error| Error::InvalidFixture {
                path: fixture_path.to_path_buf(),
                reason: format!("unable to resolve {field} {}: {error}", relative_path.display()),
            })?;
            if !canonical.starts_with(allowed_root) {
                return Err(Error::InvalidFixture {
                    path: fixture_path.to_path_buf(),
                    reason: format!(
                        "{field} resolves outside the fixture trust boundary: {}",
                        relative_path.display()
                    ),
                });
            }
            return Ok(canonical);
        }
        Ok(resolved)
    }

    pub(crate) fn validated_document_path(&self, fixture_path: &Path) -> Result<PathBuf> {
        Self::validate_fixture_relative_path(fixture_path, &self.document, "document")
    }

    fn validate_ground_truth_file(fixture_path: &Path, relative_path: Option<&Path>, kind: &str) -> Result<()> {
        let Some(relative_path) = relative_path else {
            return Ok(());
        };
        let resolved_path = Self::validate_fixture_relative_path(fixture_path, relative_path, kind)?;
        std::fs::read_to_string(&resolved_path).map_err(|error| Error::InvalidFixture {
            path: fixture_path.to_path_buf(),
            reason: format!(
                "unable to read ground truth {kind} file {} (resolved to {}): {error}",
                relative_path.display(),
                resolved_path.display()
            ),
        })?;
        Ok(())
    }

    /// Resolve document path relative to fixture file
    pub fn resolve_document_path(&self, fixture_dir: &Path) -> PathBuf {
        fixture_dir.join(&self.document)
    }

    /// Resolve ground truth path relative to fixture file
    pub fn resolve_ground_truth_path(&self, fixture_dir: &Path) -> Option<PathBuf> {
        self.ground_truth
            .as_ref()
            .and_then(|gt| gt.text_file.as_ref().map(|tf| fixture_dir.join(tf)))
    }

    /// Resolve ground truth markdown path relative to fixture file
    pub fn resolve_ground_truth_markdown_path(&self, fixture_dir: &Path) -> Option<PathBuf> {
        self.ground_truth
            .as_ref()
            .and_then(|gt| gt.markdown_file.as_ref().map(|mf| fixture_dir.join(mf)))
    }

    /// Determine if this fixture requires OCR based on file type and metadata
    pub fn requires_ocr(&self) -> bool {
        if let Some(requires_ocr) = self.metadata.get("requires_ocr").and_then(|v| v.as_bool()) {
            return requires_ocr;
        }

        matches!(
            self.file_type.to_lowercase().as_str(),
            "jpg" | "jpeg" | "png" | "gif" | "bmp" | "tiff" | "tif" | "webp" | "jp2" | "jpx" | "jpm" | "mj2"
        )
    }

    /// Return the fixture-specific OCR language code, if configured.
    pub fn ocr_language(&self) -> Option<&str> {
        self.metadata.get("ocr_language").and_then(|value| value.as_str())
    }
}

fn normalize_path(path: &Path) -> PathBuf {
    let mut normalized = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            Component::Prefix(_) | Component::RootDir | Component::Normal(_) => normalized.push(component.as_os_str()),
        }
    }
    normalized
}

fn repository_fixture_root(fixture_dir: &Path) -> Option<PathBuf> {
    fixture_dir
        .ancestors()
        .find(|ancestor| {
            let harness_root = ancestor.join("tools/benchmark-harness");
            harness_root.join("Cargo.toml").is_file() && fixture_dir.starts_with(harness_root.join("fixtures"))
        })
        .map(Path::to_path_buf)
}

/// Manages loading and accessing fixtures
pub struct FixtureManager {
    fixtures: Vec<(PathBuf, Fixture)>,
}

impl FixtureManager {
    /// Create a new empty fixture manager
    pub fn new() -> Self {
        Self { fixtures: Vec::new() }
    }

    /// Load a single fixture file
    pub fn load_fixture(&mut self, path: impl AsRef<Path>) -> Result<()> {
        let path = path.as_ref();

        if !path.exists() {
            return Err(Error::FixtureNotFound(path.to_path_buf()));
        }

        let fixture = Fixture::from_file(path)?;
        self.fixtures.push((path.to_path_buf(), fixture));

        Ok(())
    }

    /// Parse profiling fixtures from environment variable
    ///
    /// Reads the `PROFILING_FIXTURES` environment variable (comma-separated fixture names).
    /// Returns a HashSet of fixture names to use during profiling runs.
    ///
    /// # Examples
    ///
    /// ```text
    /// PROFILING_FIXTURES="pdf_small,pdf_medium,docx_simple" -> {pdf_small, pdf_medium, docx_simple}
    /// ```
    fn get_profiling_fixtures() -> Option<HashSet<String>> {
        std::env::var("PROFILING_FIXTURES")
            .ok()
            .map(|fixtures_str| {
                fixtures_str
                    .split(',')
                    .map(|s| s.trim().to_string())
                    .filter(|s| !s.is_empty())
                    .collect::<HashSet<String>>()
            })
            .filter(|set| !set.is_empty())
    }

    /// Load all fixtures from a directory (recursively)
    ///
    /// If the `PROFILING_FIXTURES` environment variable is set, only fixtures matching
    /// the specified names (comma-separated) will be loaded. Otherwise, all fixtures are loaded.
    pub fn load_fixtures_from_dir(&mut self, dir: impl AsRef<Path>) -> Result<()> {
        self.load_fixtures_from_dir_internal(dir, true)
    }

    /// Internal method for loading fixtures from a directory (with filter control)
    fn load_fixtures_from_dir_internal(&mut self, dir: impl AsRef<Path>, apply_filter: bool) -> Result<()> {
        let dir = dir.as_ref();

        if !dir.exists() {
            return Err(Error::FixtureNotFound(dir.to_path_buf()));
        }

        let mut all_fixtures: Vec<PathBuf> = Vec::new();

        for entry in std::fs::read_dir(dir)? {
            let entry = entry?;
            let path = entry.path();

            if path.is_dir() {
                let mut temp_manager = FixtureManager::new();
                temp_manager.load_fixtures_from_dir_internal(&path, false)?;
                for (fixture_path, _) in temp_manager.fixtures {
                    all_fixtures.push(fixture_path);
                }
            } else if path.extension().and_then(|s| s.to_str()) == Some("json") && !is_split_sidecar(&path) {
                all_fixtures.push(path);
            }
        }
        all_fixtures.sort();

        let total_fixtures = all_fixtures.len();
        let mut failed_fixtures: Vec<(PathBuf, String)> = Vec::new();

        if apply_filter {
            if let Some(profiling_set) = Self::get_profiling_fixtures() {
                let mut loaded_count = 0;
                let mut fixture_names = Vec::new();

                for fixture_path in &all_fixtures {
                    if let Some(stem) = fixture_path.file_stem().and_then(|s| s.to_str())
                        && profiling_set.contains(stem)
                    {
                        match self.load_fixture(fixture_path) {
                            Ok(()) => {
                                loaded_count += 1;
                                fixture_names.push(stem.to_string());
                            }
                            Err(e) => {
                                failed_fixtures.push((fixture_path.clone(), e.to_string()));
                            }
                        }
                    }
                }

                if loaded_count > 0 {
                    fixture_names.sort();
                    eprintln!(
                        "Profiling mode: Using {} of {} fixtures: {}",
                        loaded_count,
                        total_fixtures,
                        fixture_names.join(", ")
                    );
                } else {
                    eprintln!(
                        "Warning: PROFILING_FIXTURES set but no matching fixtures found. \
                        Loading all {} fixtures.",
                        total_fixtures
                    );
                    for fixture_path in all_fixtures {
                        match self.load_fixture(&fixture_path) {
                            Ok(()) => {}
                            Err(e) => {
                                failed_fixtures.push((fixture_path.clone(), e.to_string()));
                            }
                        }
                    }
                }
            } else {
                for fixture_path in all_fixtures {
                    match self.load_fixture(&fixture_path) {
                        Ok(()) => {}
                        Err(e) => {
                            failed_fixtures.push((fixture_path.clone(), e.to_string()));
                        }
                    }
                }
            }
        } else {
            for fixture_path in all_fixtures {
                match self.load_fixture(&fixture_path) {
                    Ok(()) => {}
                    Err(e) => {
                        failed_fixtures.push((fixture_path.clone(), e.to_string()));
                    }
                }
            }
        }

        if !failed_fixtures.is_empty() {
            let first_path = failed_fixtures[0].0.clone();
            let details = failed_fixtures
                .iter()
                .take(10)
                .map(|(path, error)| format!("{}: {error}", path.display()))
                .collect::<Vec<_>>()
                .join("; ");
            return Err(Error::InvalidFixture {
                path: first_path,
                reason: format!(
                    "{} of {} requested fixtures failed validation: {}",
                    failed_fixtures.len(),
                    total_fixtures,
                    details
                ),
            });
        }

        Ok(())
    }

    /// Get all loaded fixtures
    pub fn fixtures(&self) -> &[(PathBuf, Fixture)] {
        &self.fixtures
    }

    /// Get count of loaded fixtures
    pub fn len(&self) -> usize {
        self.fixtures.len()
    }

    /// Check if empty
    pub fn is_empty(&self) -> bool {
        self.fixtures.is_empty()
    }

    /// Filter fixtures by file type
    pub fn filter_by_type(&self, file_types: &[String]) -> Vec<(PathBuf, Fixture)> {
        self.fixtures
            .iter()
            .filter(|(_, fixture)| file_types.contains(&fixture.file_type))
            .cloned()
            .collect()
    }

    /// Retain only the fixtures belonging to shard `index` of `total` shards.
    ///
    /// Fixtures are sorted by path for deterministic ordering, then assigned
    /// round-robin to shards. This ensures even distribution across shards
    /// regardless of file type or size ordering.
    ///
    /// `index` is 1-based (1..=total).
    pub fn retain_shard(&mut self, index: usize, total: usize) {
        assert!(index >= 1 && index <= total, "shard index must be 1..=total");
        self.fixtures.sort_by(|a, b| a.0.cmp(&b.0));
        let shard_index = index - 1;
        self.fixtures = self
            .fixtures
            .drain(..)
            .enumerate()
            .filter(|(i, _)| i % total == shard_index)
            .map(|(_, f)| f)
            .collect();
    }
}

impl Default for FixtureManager {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;
    use tempfile::TempDir;

    static ENV_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn test_fixture_validation() {
        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec!["xberg".to_string()],
            metadata: HashMap::new(),
            ground_truth: None,
        };

        assert!(fixture.validate(Path::new("fixture.json")).is_ok());
    }

    #[test]
    fn fixture_validation_accepts_apple_preview_ground_truth() {
        let root = TempDir::new().unwrap();
        let fixture_path = root.path().join("fixture.json");
        std::fs::write(root.path().join("expected.txt"), "cell value").unwrap();
        let fixture = Fixture {
            document: PathBuf::from("test.numbers"),
            file_type: "numbers".to_string(),
            file_size: 1024,
            expected_frameworks: vec!["xberg".to_string()],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: Some(PathBuf::from("expected.txt")),
                markdown_file: None,
                fields_json: None,
                formulas_json: None,
                source: "apple_preview".to_string(),
            }),
        };

        assert!(fixture.validate(&fixture_path).is_ok());
    }

    #[test]
    fn fixture_ocr_language_validation_accepts_codes_and_rejects_paths() {
        let make_fixture = |language: &str| Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec!["xberg".to_string()],
            metadata: HashMap::from([("ocr_language".to_string(), serde_json::json!(language))]),
            ground_truth: None,
        };

        assert!(make_fixture(" deu + eng ").validate(Path::new("fixture.json")).is_ok());
        assert!(make_fixture("+").validate(Path::new("fixture.json")).is_err());
        assert!(make_fixture("../deu").validate(Path::new("fixture.json")).is_err());
    }

    #[test]
    fn test_absolute_path_rejected() {
        #[cfg(windows)]
        let absolute_path = PathBuf::from("C:\\absolute\\path\\test.pdf");
        #[cfg(not(windows))]
        let absolute_path = PathBuf::from("/absolute/path/test.pdf");

        let fixture = Fixture {
            document: absolute_path,
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: None,
        };

        assert!(fixture.validate(Path::new("fixture.json")).is_err());
    }

    #[test]
    fn fixture_paths_cannot_escape_a_standalone_fixture_tree() {
        let root = TempDir::new().unwrap();
        let fixture_dir = root.path().join("fixtures");
        std::fs::create_dir(&fixture_dir).unwrap();
        std::fs::write(root.path().join("secret.txt"), "not fixture data").unwrap();
        let fixture_path = fixture_dir.join("escape.json");
        let fixture = Fixture {
            document: PathBuf::from("../secret.txt"),
            file_type: "txt".to_string(),
            file_size: 16,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: None,
        };

        let error = fixture.validate(&fixture_path).unwrap_err().to_string();
        assert!(error.contains("document escapes the fixture trust boundary"), "{error}");
    }

    #[test]
    fn repository_fixture_boundary_is_derived_from_the_runtime_path() {
        let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
        let fixture_dir = manifest_dir.join("fixtures/pdf").canonicalize().unwrap();
        let expected_root = manifest_dir.join("../..").canonicalize().unwrap();

        assert_eq!(repository_fixture_root(&fixture_dir), Some(expected_root));
    }

    #[test]
    fn ground_truth_paths_cannot_escape_a_standalone_fixture_tree() {
        let root = TempDir::new().unwrap();
        let fixture_dir = root.path().join("fixtures");
        std::fs::create_dir(&fixture_dir).unwrap();
        std::fs::write(root.path().join("secret.txt"), "not fixture data").unwrap();
        let fixture_path = fixture_dir.join("escape.json");
        let fixture = Fixture {
            document: PathBuf::from("document.txt"),
            file_type: "txt".to_string(),
            file_size: 16,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: Some(PathBuf::from("../secret.txt")),
                markdown_file: None,
                fields_json: None,
                formulas_json: None,
                source: "manual".to_string(),
            }),
        };

        let error = fixture.validate(&fixture_path).unwrap_err().to_string();
        assert!(
            error.contains("ground_truth.text_file escapes the fixture trust boundary"),
            "{error}"
        );
    }

    #[cfg(unix)]
    #[test]
    fn fixture_paths_cannot_escape_through_symlinks() {
        use std::os::unix::fs::symlink;

        let root = TempDir::new().unwrap();
        let fixture_dir = root.path().join("fixtures");
        std::fs::create_dir(&fixture_dir).unwrap();
        std::fs::write(root.path().join("secret.txt"), "not fixture data").unwrap();
        symlink(root.path().join("secret.txt"), fixture_dir.join("document.txt")).unwrap();
        let fixture_path = fixture_dir.join("escape.json");
        let fixture = Fixture {
            document: PathBuf::from("document.txt"),
            file_type: "txt".to_string(),
            file_size: 16,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: None,
        };

        let error = fixture.validate(&fixture_path).unwrap_err().to_string();
        assert!(
            error.contains("document resolves outside the fixture trust boundary"),
            "{error}"
        );
    }

    #[test]
    fn test_fixture_manager_load() {
        let temp_dir = TempDir::new().unwrap();
        let fixture_path = temp_dir.path().join("test.json");

        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: None,
        };

        std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();

        let mut manager = FixtureManager::new();
        assert!(manager.load_fixture(&fixture_path).is_ok());
        assert_eq!(manager.len(), 1);
    }

    #[test]
    fn test_profiling_fixtures_with_env_var() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();

        let fixtures = vec!["pdf_small", "pdf_medium", "docx_simple", "html_simple"];
        for fixture_name in &fixtures {
            let fixture_path = temp_dir.path().join(format!("{}.json", fixture_name));
            let fixture = Fixture {
                document: PathBuf::from(format!("{}.pdf", fixture_name)),
                file_type: "pdf".to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };
            std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();
        }

        unsafe {
            std::env::set_var("PROFILING_FIXTURES", "pdf_small,docx_simple");
        }

        let mut manager = FixtureManager::new();
        manager.load_fixtures_from_dir(temp_dir.path()).unwrap();

        assert_eq!(manager.len(), 2);

        let loaded_names: Vec<String> = manager
            .fixtures()
            .iter()
            .filter_map(|(path, _)| path.file_stem().and_then(|s| s.to_str()).map(|s| s.to_string()))
            .collect();

        assert!(loaded_names.contains(&"pdf_small".to_string()));
        assert!(loaded_names.contains(&"docx_simple".to_string()));
        assert!(!loaded_names.contains(&"pdf_medium".to_string()));
        assert!(!loaded_names.contains(&"html_simple".to_string()));

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }
    }

    #[test]
    fn test_profiling_fixtures_all_when_env_not_set() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();

        let fixtures = vec!["pdf_small", "pdf_medium", "docx_simple"];
        for fixture_name in &fixtures {
            let fixture_path = temp_dir.path().join(format!("{}.json", fixture_name));
            let fixture = Fixture {
                document: PathBuf::from(format!("{}.pdf", fixture_name)),
                file_type: "pdf".to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };
            std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();
        }

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }

        let mut manager = FixtureManager::new();
        manager.load_fixtures_from_dir(temp_dir.path()).unwrap();

        assert_eq!(manager.len(), 3);
    }

    #[test]
    fn test_fixture_discovery_is_sorted() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();
        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }

        for fixture_name in ["zeta", "alpha", "middle"] {
            let fixture = Fixture {
                document: PathBuf::from(format!("{fixture_name}.pdf")),
                file_type: "pdf".to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };
            std::fs::write(
                temp_dir.path().join(format!("{fixture_name}.json")),
                serde_json::to_string(&fixture).unwrap(),
            )
            .unwrap();
        }

        let mut manager = FixtureManager::new();
        manager.load_fixtures_from_dir(temp_dir.path()).unwrap();
        let loaded_names: Vec<_> = manager
            .fixtures()
            .iter()
            .map(|(path, _)| path.file_stem().unwrap().to_string_lossy().into_owned())
            .collect();
        assert_eq!(loaded_names, ["alpha", "middle", "zeta"]);
    }

    #[test]
    fn test_profiling_fixtures_with_whitespace() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();

        let fixtures = vec!["pdf_small", "pdf_medium", "docx_simple"];
        for fixture_name in &fixtures {
            let fixture_path = temp_dir.path().join(format!("{}.json", fixture_name));
            let fixture = Fixture {
                document: PathBuf::from(format!("{}.pdf", fixture_name)),
                file_type: "pdf".to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };
            std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();
        }

        unsafe {
            std::env::set_var("PROFILING_FIXTURES", "pdf_small , pdf_medium , docx_simple");
        }

        let mut manager = FixtureManager::new();
        manager.load_fixtures_from_dir(temp_dir.path()).unwrap();

        assert_eq!(manager.len(), 3);

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }
    }

    #[test]
    fn test_profiling_fixtures_partial_match() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();

        let fixtures = vec!["pdf_small", "pdf_medium", "docx_simple"];
        for fixture_name in &fixtures {
            let fixture_path = temp_dir.path().join(format!("{}.json", fixture_name));
            let fixture = Fixture {
                document: PathBuf::from(format!("{}.pdf", fixture_name)),
                file_type: "pdf".to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };
            std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();
        }

        unsafe {
            std::env::set_var("PROFILING_FIXTURES", "pdf_small,nonexistent_fixture");
        }

        let mut manager = FixtureManager::new();
        manager.load_fixtures_from_dir(temp_dir.path()).unwrap();

        assert_eq!(manager.len(), 1);

        let loaded_names: Vec<String> = manager
            .fixtures()
            .iter()
            .filter_map(|(path, _)| path.file_stem().and_then(|s| s.to_str()).map(|s| s.to_string()))
            .collect();

        assert!(loaded_names.contains(&"pdf_small".to_string()));

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }
    }

    #[test]
    fn test_requires_ocr_for_image_types() {
        let image_types = vec!["jpg", "jpeg", "png", "gif", "bmp", "tiff", "webp"];

        for file_type in image_types {
            let fixture = Fixture {
                document: PathBuf::from(format!("test.{}", file_type)),
                file_type: file_type.to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };

            assert!(
                fixture.requires_ocr(),
                "Expected file type {} to require OCR",
                file_type
            );
        }
    }

    #[test]
    fn test_requires_ocr_for_non_image_types() {
        let non_image_types = vec!["pdf", "docx", "txt", "html", "md"];

        for file_type in non_image_types {
            let fixture = Fixture {
                document: PathBuf::from(format!("test.{}", file_type)),
                file_type: file_type.to_string(),
                file_size: 1024,
                expected_frameworks: vec![],
                metadata: HashMap::new(),
                ground_truth: None,
            };

            assert!(
                !fixture.requires_ocr(),
                "Expected file type {} to not require OCR",
                file_type
            );
        }
    }

    #[test]
    fn test_requires_ocr_explicit_metadata_true() {
        let mut metadata = HashMap::new();
        metadata.insert("requires_ocr".to_string(), serde_json::json!(true));

        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata,
            ground_truth: None,
        };

        assert!(fixture.requires_ocr());
    }

    #[test]
    fn test_requires_ocr_explicit_metadata_false() {
        let mut metadata = HashMap::new();
        metadata.insert("requires_ocr".to_string(), serde_json::json!(false));

        let fixture = Fixture {
            document: PathBuf::from("test.png"),
            file_type: "png".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata,
            ground_truth: None,
        };

        assert!(!fixture.requires_ocr());
    }

    #[test]
    fn test_requires_ocr_case_insensitive() {
        let fixture = Fixture {
            document: PathBuf::from("test.JPG"),
            file_type: "JPG".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: None,
        };

        assert!(fixture.requires_ocr());
    }

    #[test]
    fn test_ground_truth_file_existence_validation() {
        let temp_dir = TempDir::new().unwrap();
        let fixture_path = temp_dir.path().join("test.json");

        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: Some(PathBuf::from("nonexistent_ground_truth.txt")),
                markdown_file: None,
                fields_json: None,
                formulas_json: None,
                source: "manual".to_string(),
            }),
        };

        std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();

        let result = Fixture::from_file(&fixture_path);
        assert!(result.is_err());
        match result {
            Err(Error::InvalidFixture { reason, .. }) => {
                assert!(reason.contains("unable to read ground truth text file"));
            }
            _ => panic!("Expected InvalidFixture error for unreadable ground truth"),
        }
    }

    #[test]
    fn test_markdown_only_ground_truth_is_validated() {
        let temp_dir = TempDir::new().unwrap();
        let fixture_path = temp_dir.path().join("test.json");
        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: None,
                markdown_file: Some(PathBuf::from("missing.md")),
                fields_json: None,
                formulas_json: None,
                source: "markdown_file".to_string(),
            }),
        };
        std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();

        let error = Fixture::from_file(&fixture_path).unwrap_err();
        assert!(matches!(
            error,
            Error::InvalidFixture { reason, .. }
                if reason.contains("unable to read ground truth markdown file")
        ));
    }

    #[test]
    fn test_non_utf8_ground_truth_is_rejected() {
        let temp_dir = TempDir::new().unwrap();
        let fixture_path = temp_dir.path().join("test.json");
        std::fs::write(temp_dir.path().join("ground_truth.txt"), [0xff, 0xfe]).unwrap();
        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: Some(PathBuf::from("ground_truth.txt")),
                markdown_file: None,
                fields_json: None,
                formulas_json: None,
                source: "manual".to_string(),
            }),
        };
        std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();

        let error = Fixture::from_file(&fixture_path).unwrap_err();
        assert!(matches!(
            error,
            Error::InvalidFixture { reason, .. }
                if reason.contains("unable to read ground truth text file")
        ));
    }

    #[test]
    fn test_ground_truth_file_existence_validation_success() {
        let temp_dir = TempDir::new().unwrap();
        let fixture_path = temp_dir.path().join("test.json");
        let ground_truth_path = temp_dir.path().join("ground_truth.txt");

        std::fs::write(&ground_truth_path, "Sample ground truth text").unwrap();

        let fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: Some(PathBuf::from("ground_truth.txt")),
                markdown_file: None,
                fields_json: None,
                formulas_json: None,
                source: "manual".to_string(),
            }),
        };

        std::fs::write(&fixture_path, serde_json::to_string(&fixture).unwrap()).unwrap();

        let result = Fixture::from_file(&fixture_path);
        assert!(result.is_ok());
    }

    #[test]
    fn test_fixture_load_with_mixed_success_and_failure() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();

        let valid_fixture_path = temp_dir.path().join("valid.json");
        let valid_fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: None,
        };
        std::fs::write(&valid_fixture_path, serde_json::to_string(&valid_fixture).unwrap()).unwrap();

        let invalid_fixture_path = temp_dir.path().join("invalid.json");
        let invalid_fixture = Fixture {
            document: PathBuf::from("test.pdf"),
            file_type: "pdf".to_string(),
            file_size: 1024,
            expected_frameworks: vec![],
            metadata: HashMap::new(),
            ground_truth: Some(GroundTruth {
                text_file: Some(PathBuf::from("nonexistent.txt")),
                markdown_file: None,
                fields_json: None,
                formulas_json: None,
                source: "manual".to_string(),
            }),
        };
        std::fs::write(&invalid_fixture_path, serde_json::to_string(&invalid_fixture).unwrap()).unwrap();

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }

        let mut manager = FixtureManager::new();
        let result = manager.load_fixtures_from_dir(temp_dir.path());
        assert!(result.is_err());

        assert_eq!(manager.len(), 1);
    }

    #[test]
    fn nested_invalid_fixture_fails_the_entire_directory_load() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();
        let nested_dir = temp_dir.path().join("nested");
        std::fs::create_dir(&nested_dir).unwrap();
        std::fs::write(
            temp_dir.path().join("valid.json"),
            serde_json::json!({
                "document": "valid.pdf",
                "file_type": "pdf",
                "file_size": 1
            })
            .to_string(),
        )
        .unwrap();
        std::fs::write(nested_dir.join("malformed.json"), "{not valid json").unwrap();

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }
        let mut manager = FixtureManager::new();
        let error = manager.load_fixtures_from_dir(temp_dir.path()).unwrap_err();

        assert!(error.to_string().contains("malformed.json"));
        assert_eq!(
            manager.len(),
            0,
            "nested failure must abort before the parent corpus loads"
        );
    }

    #[test]
    fn directory_load_skips_split_sidecars() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();
        std::fs::write(
            temp_dir.path().join("fixture.json"),
            serde_json::json!({
                "document": "document.pdf",
                "file_type": "pdf",
                "file_size": 1
            })
            .to_string(),
        )
        .unwrap();
        std::fs::write(
            temp_dir.path().join("document.split.json"),
            serde_json::json!({
                "document": "document.pdf",
                "boundaries": [{"start_page": 1, "end_page": 2}]
            })
            .to_string(),
        )
        .unwrap();
        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }
        let mut manager = FixtureManager::new();
        manager.load_fixtures_from_dir(temp_dir.path()).unwrap();

        assert_eq!(manager.len(), 1);
        assert_eq!(manager.fixtures()[0].0.file_name().unwrap(), "fixture.json");
    }

    #[test]
    fn directory_load_rejects_partial_fixture_schema() {
        let _lock = ENV_LOCK.lock().unwrap();
        let temp_dir = TempDir::new().unwrap();
        std::fs::write(
            temp_dir.path().join("incomplete.json"),
            serde_json::json!({"document": "document.pdf"}).to_string(),
        )
        .unwrap();

        unsafe {
            std::env::remove_var("PROFILING_FIXTURES");
        }
        let mut manager = FixtureManager::new();
        let error = manager.load_fixtures_from_dir(temp_dir.path()).unwrap_err();

        assert!(error.to_string().contains("incomplete.json"));
    }
}
