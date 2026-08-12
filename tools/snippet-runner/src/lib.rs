// Internal dev tool: stdout IS this validator's report output, so raw printing is intentional. ~keep
#![allow(clippy::print_stdout, clippy::print_stderr)]

pub mod discovery;
pub mod error;
pub mod output;
pub mod parser;
pub mod runner;
pub mod types;
pub mod validators;
