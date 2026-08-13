# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-rc.32] - 2026-07-22

### Added

- Initial `@xberg-io/langchain-xberg` release with the `XbergLoader` document loader for LangChain.js.
- Loads a single file, a list of files, a directory glob, or raw bytes via the `@xberg-io/xberg` native binding.
- Emits one `Document` per source, per chunk (when chunking is enabled), or per page (when page splitting is enabled),
  with flattened document metadata, tables, keywords, and processing warnings.
