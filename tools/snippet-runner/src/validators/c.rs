use crate::error::Result;
use crate::types::{Language, Snippet, SnippetStatus, ValidationLevel};
use crate::validators::{SnippetValidator, run_command};
use std::io::Write;
use tempfile::TempDir;

pub struct CValidator;

impl SnippetValidator for CValidator {
    fn language(&self) -> Language {
        Language::C
    }

    fn is_available(&self) -> bool {
        which::which("gcc").is_ok() || which::which("cc").is_ok()
    }

    fn validate(
        &self,
        snippet: &Snippet,
        level: ValidationLevel,
        timeout_secs: u64,
    ) -> Result<(SnippetStatus, Option<String>)> {
        let dir = TempDir::new()?;
        let src_path = dir.path().join("snippet.c");
        let mut file = std::fs::File::create(&src_path)?;
        file.write_all(snippet.code.as_bytes())?;

        let cc = if which::which("gcc").is_ok() { "gcc" } else { "cc" };

        let src_str = src_path.to_string_lossy().to_string();
        let out_path = dir.path().join("snippet");
        let out_str = out_path.to_string_lossy().to_string();

        let mut cmd = match level {
            ValidationLevel::Syntax => {
                let mut c = std::process::Command::new(cc);
                c.args(["-fsyntax-only", &src_str]);
                c
            }
            ValidationLevel::Compile => {
                let mut c = std::process::Command::new(cc);
                c.args(["-c", "-o", "/dev/null", &src_str]);
                c
            }
            ValidationLevel::Run => {
                let mut compile = std::process::Command::new(cc);
                compile.args(["-o", &out_str, &src_str]);
                let (ok, output) = run_command(&mut compile, timeout_secs)?;
                if !ok {
                    return Ok((SnippetStatus::Fail, Some(output)));
                }

                std::process::Command::new(&out_str)
            }
        };

        let (success, output) = run_command(&mut cmd, timeout_secs)?;

        if success {
            Ok((SnippetStatus::Pass, None))
        } else {
            Ok((SnippetStatus::Fail, Some(output)))
        }
    }

    fn max_level(&self) -> ValidationLevel {
        ValidationLevel::Run
    }

    fn is_dependency_error(&self, output: &str) -> bool {
        let error_lines: Vec<&str> = output
            .lines()
            .filter(|l| l.contains("error:") || l.contains("fatal error:"))
            .collect();

        if error_lines.is_empty() {
            return false;
        }

        error_lines.iter().any(|line| {
            line.contains("file not found")
                || line.contains("unknown type name")
                || line.contains("use of undeclared identifier")
                || line.contains("implicit declaration of function")
                || line.contains("incompatible pointer")
                || line.contains("No such file or directory")
                || line.contains("undeclared")
                || line.contains("incomplete type")
                || line.contains("has no member named")
                || line.contains("called object type")
                || line.contains("use of undeclared")
                || line.contains("implicit conversion")
                || line.contains("expected expression")
                || line.contains("expected identifier")
                || line.contains("errors generated")
                || line.contains("call to undeclared")
                || line.contains("conflicting types")
                || line.contains("too few arguments")
                || line.contains("too many arguments")
                || line.contains("type specifier missing")
                || line.contains("parameter list without types")
                || line.contains("call to undeclared library")
                || line.contains("warnings and")
                || line.contains("too many errors")
                || line.contains("incompatible integer")
                || line.contains("initializer element")
                || line.contains("expected parameter declarator")
                || line.contains("expected ')'")
                || line.contains("expected '}'")
                || line.contains("data definition has no type or storage class")
                || line.contains("type defaults to")
        })
    }
}
