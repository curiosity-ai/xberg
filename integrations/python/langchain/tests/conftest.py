"""Shared test fixtures and real-object builders for langchain-xberg.

These builders construct genuine Xberg result objects (``ExtractionResult`` /
``ExtractedDocument`` and friends) from the compiled ``xberg._xberg`` module, so
tests exercise the loader against the real result shape rather than fabricated
mocks. The public ``xberg.*`` names for data holders resolve to option
dataclasses that the native constructors reject, so builders use ``xberg._xberg``
directly.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

import pytest
import xberg._xberg as _rust


@pytest.fixture
def sample_txt_path() -> Path:
    """Path to a small test text file."""
    return Path(__file__).parent / "fixtures" / "sample.txt"


@pytest.fixture
def sample_pdf_path() -> Path:
    """Path to a small test PDF."""
    return Path(__file__).parent / "fixtures" / "sample.pdf"


@pytest.fixture
def sample_docx_path() -> Path:
    """Path to a small test DOCX file."""
    return Path(__file__).parent / "fixtures" / "sample.docx"


@pytest.fixture
def sample_html_path() -> Path:
    """Path to a small test HTML file."""
    return Path(__file__).parent / "fixtures" / "sample.html"


@pytest.fixture
def sample_bytes() -> bytes:
    """Sample text bytes for bytes-mode testing."""
    return b"Hello, this is sample text content for testing."


@pytest.fixture
def tmp_dir_with_files(tmp_path: Path) -> Path:
    """Temporary directory with mixed file types for glob testing."""
    (tmp_path / "file1.txt").write_text("Text file 1")
    (tmp_path / "file2.txt").write_text("Text file 2")
    (tmp_path / "subdir").mkdir()
    (tmp_path / "subdir" / "file3.txt").write_text("Text file 3")
    return tmp_path


def make_table(
    cells: list[list[str]] | None = None,
    markdown: str = "| A | B |\n|---|---|\n| 1 | 2 |",
    page_number: int = 1,
) -> _rust.Table:
    """Build a real Xberg Table."""
    return _rust.Table(
        cells=cells if cells is not None else [["A", "B"], ["1", "2"]],
        markdown=markdown,
        page_number=page_number,
    )


def make_keyword(text: str = "python", score: float = 0.95, algorithm: str = "yake") -> _rust.Keyword:
    """Build a real Xberg Keyword."""
    return _rust.Keyword(text=text, score=score, algorithm=_rust.KeywordAlgorithm(algorithm))


def make_page(
    page_number: int,
    content: str,
    *,
    tables: list[_rust.Table] | None = None,
    is_blank: bool | None = None,
) -> _rust.PageContent:
    """Build a real Xberg PageContent."""
    return _rust.PageContent(
        page_number=page_number,
        content=content,
        tables=tables or [],
        image_indices=[],
        is_blank=is_blank,
    )


def make_chunk(
    content: str,
    *,
    chunk_index: int,
    total_chunks: int,
    heading_path: list[str] | None = None,
    first_page: int | None = None,
    last_page: int | None = None,
    token_count: int | None = None,
    chunk_type: str = "unknown",
) -> _rust.Chunk:
    """Build a real Xberg Chunk."""
    metadata = _rust.ChunkMetadata(
        byte_start=0,
        byte_end=len(content),
        chunk_index=chunk_index,
        total_chunks=total_chunks,
        heading_path=heading_path or [],
        image_indices=[],
        node_ids=[],
        page_spans=[],
        classifications=[],
        token_count=token_count,
        first_page=first_page,
        last_page=last_page,
    )
    return _rust.Chunk(content=content, chunk_type=_rust.ChunkType(chunk_type), metadata=metadata)


def make_document(
    content: str = "Extracted text content",
    mime_type: str = "text/plain",
    *,
    metadata: dict[str, Any] | None = None,
    tables: list[_rust.Table] | None = None,
    pages: list[_rust.PageContent] | None = None,
    chunks: list[_rust.Chunk] | None = None,
    quality_score: float | None = 1.0,
    detected_languages: list[str] | None = None,
    extracted_keywords: list[_rust.Keyword] | None = None,
    processing_warnings: list[Any] | None = None,
    page_count: int = 1,
) -> _rust.ExtractedDocument:
    """Build a real Xberg ExtractedDocument with sensible defaults."""
    warnings: list[_rust.ProcessingWarning] = []
    for warning in processing_warnings or []:
        if isinstance(warning, str):
            warnings.append(_rust.ProcessingWarning(source="extraction", message=warning))
        elif isinstance(warning, dict):
            warnings.append(
                _rust.ProcessingWarning(
                    source=warning.get("source", "extraction"),
                    message=warning.get("message", ""),
                )
            )
        else:
            warnings.append(warning)

    return _rust.ExtractedDocument(
        content=content,
        mime_type=mime_type,
        metadata=_rust.Metadata(**(metadata or {})),
        counts=_rust.DocumentCounts(pages=page_count, tables=len(tables or []), images=0),
        tables=tables or [],
        pages=pages,
        chunks=chunks,
        quality_score=quality_score,
        detected_languages=detected_languages,
        extracted_keywords=extracted_keywords,
        processing_warnings=warnings,
    )


def make_error(
    index: int,
    source: str,
    message: str = "extraction failed",
    *,
    code: int = 1001,
    error_type: str = "ParsingError",
) -> _rust.ExtractionErrorItem:
    """Build a real Xberg ExtractionErrorItem."""
    return _rust.ExtractionErrorItem(
        index=index,
        code=code,
        error_type=error_type,
        source=source,
        message=message,
    )


def make_result(
    documents: list[_rust.ExtractedDocument] | None = None,
    errors: list[_rust.ExtractionErrorItem] | None = None,
) -> _rust.ExtractionResult:
    """Build a real Xberg ExtractionResult envelope."""
    return _rust.ExtractionResult(results=documents or [], errors=errors or [])
