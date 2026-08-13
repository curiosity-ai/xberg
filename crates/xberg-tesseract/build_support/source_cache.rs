use std::fs;
use std::io;
use std::path::{Path, PathBuf};

const SOURCE_ROOT_MARKER: &str = "CMakeLists.txt";

#[derive(Debug, Eq, PartialEq)]
pub(crate) struct PreparedSourceTree {
    pub(crate) path: PathBuf,
    pub(crate) downloaded: bool,
}

pub(crate) fn source_tree_is_complete(source_dir: &Path) -> bool {
    source_dir.is_dir() && source_dir.join(SOURCE_ROOT_MARKER).is_file()
}

pub(crate) fn prepare_source_tree(
    third_party_dir: &Path,
    source_name: &str,
    download: impl FnOnce(&Path),
) -> io::Result<PreparedSourceTree> {
    fs::create_dir_all(third_party_dir)?;

    let source_dir = third_party_dir.join(source_name);
    if source_tree_is_complete(&source_dir) {
        return Ok(PreparedSourceTree {
            path: source_dir,
            downloaded: false,
        });
    }

    remove_incomplete_source(&source_dir)?;
    download(third_party_dir);

    if !source_tree_is_complete(&source_dir) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!(
                "downloaded {source_name} source is incomplete: missing {}",
                source_dir.join(SOURCE_ROOT_MARKER).display()
            ),
        ));
    }

    Ok(PreparedSourceTree {
        path: source_dir,
        downloaded: true,
    })
}

fn remove_incomplete_source(source_dir: &Path) -> io::Result<()> {
    let metadata = match fs::symlink_metadata(source_dir) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error),
    };

    if metadata.file_type().is_dir() && !metadata.file_type().is_symlink() {
        fs::remove_dir_all(source_dir)
    } else {
        fs::remove_file(source_dir)
    }
}
