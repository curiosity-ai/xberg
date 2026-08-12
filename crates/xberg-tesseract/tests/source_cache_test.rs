#[path = "../build_support/source_cache.rs"]
mod source_cache;

use source_cache::{prepare_source_tree, source_tree_is_complete};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const SOURCE_NAME: &str = "leptonica";

#[test]
fn should_download_when_source_tree_is_missing() {
    let temp_dir = TestDir::new();
    let third_party_dir = temp_dir.path().join("third_party");

    let prepared = prepare_source_tree(&third_party_dir, SOURCE_NAME, |download_dir| {
        create_complete_source(download_dir, SOURCE_NAME);
    })
    .expect("prepare missing source tree");

    assert!(prepared.downloaded, "missing source should be downloaded");
    assert_eq!(prepared.path, third_party_dir.join(SOURCE_NAME));
    assert!(source_tree_is_complete(&prepared.path));
}

#[test]
fn should_replace_empty_source_tree() {
    let temp_dir = TestDir::new();
    let third_party_dir = temp_dir.path().join("third_party");
    let source_dir = third_party_dir.join(SOURCE_NAME);
    let sibling_dir = third_party_dir.join("tesseract");
    fs::create_dir_all(&source_dir).expect("create empty source tree");
    fs::create_dir_all(&sibling_dir).expect("create sibling source tree");
    fs::write(sibling_dir.join("keep.txt"), "preserve sibling").expect("write sibling sentinel");

    let prepared = prepare_source_tree(&third_party_dir, SOURCE_NAME, |download_dir| {
        assert!(!source_dir.exists(), "empty source should be removed before download");
        create_complete_source(download_dir, SOURCE_NAME);
    })
    .expect("replace empty source tree");

    assert!(prepared.downloaded, "empty source should be downloaded again");
    assert!(
        sibling_dir.join("keep.txt").is_file(),
        "sibling cache must be preserved"
    );
}

#[test]
fn should_replace_incomplete_source_tree() {
    let temp_dir = TestDir::new();
    let third_party_dir = temp_dir.path().join("third_party");
    let source_dir = third_party_dir.join(SOURCE_NAME);
    fs::create_dir_all(&source_dir).expect("create incomplete source tree");
    fs::write(source_dir.join("README.md"), "partial archive").expect("write partial source file");

    let prepared = prepare_source_tree(&third_party_dir, SOURCE_NAME, |download_dir| {
        assert!(
            !source_dir.exists(),
            "incomplete source should be removed before download"
        );
        create_complete_source(download_dir, SOURCE_NAME);
    })
    .expect("replace incomplete source tree");

    assert!(prepared.downloaded, "incomplete source should be downloaded again");
    assert!(
        !prepared.path.join("README.md").exists(),
        "partial source contents must be removed"
    );
}

#[test]
fn should_reuse_complete_source_tree() {
    let temp_dir = TestDir::new();
    let third_party_dir = temp_dir.path().join("third_party");
    create_complete_source(&third_party_dir, SOURCE_NAME);
    let sentinel = third_party_dir.join(SOURCE_NAME).join("README.md");
    fs::write(&sentinel, "cached source").expect("write cached source sentinel");
    let mut download_called = false;

    let prepared = prepare_source_tree(&third_party_dir, SOURCE_NAME, |_| {
        download_called = true;
    })
    .expect("reuse complete source tree");

    assert!(!prepared.downloaded, "complete source should be reused");
    assert!(!download_called, "download must not run for a complete source");
    assert!(sentinel.is_file(), "reused source contents must be preserved");
}

fn create_complete_source(third_party_dir: &Path, source_name: &str) {
    let source_dir = third_party_dir.join(source_name);
    fs::create_dir_all(&source_dir).expect("create complete source tree");
    fs::write(source_dir.join("CMakeLists.txt"), "cmake_minimum_required(VERSION 3.5)").expect("write source marker");
}

struct TestDir {
    path: PathBuf,
}

impl TestDir {
    fn new() -> Self {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock should follow Unix epoch")
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "xberg-source-cache-test-{}-{:?}-{unique}",
            std::process::id(),
            std::thread::current().id()
        ));
        fs::create_dir(&path).expect("create temporary test directory");
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for TestDir {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.path).expect("remove temporary test directory");
    }
}
