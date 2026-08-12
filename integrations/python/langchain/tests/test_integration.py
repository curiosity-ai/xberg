"""Integration tests — real xberg extraction, no mocking."""

from pathlib import Path

import pytest
from langchain_core.documents import Document
from xberg import ChunkingConfig, ExtractionConfig, PageConfig, XbergError

from langchain_xberg import XbergLoader

pytestmark = pytest.mark.integration

FIXTURES = Path(__file__).parent / "fixtures"


def _assert_valid_documents(
    docs: list[Document],
    *,
    min_count: int = 1,
    expected_source: str | None = None,
) -> None:
    """Shared assertion helper for the LangChain Document contract."""
    assert len(docs) >= min_count
    for doc in docs:
        assert isinstance(doc, Document)
        assert doc.page_content
        assert isinstance(doc.metadata, dict)
        assert "source" in doc.metadata
        if expected_source:
            assert expected_source in str(doc.metadata["source"])


def test_load_txt() -> None:
    loader = XbergLoader(file_path=FIXTURES / "sample.txt")
    docs = loader.load()

    _assert_valid_documents(docs, expected_source="sample.txt")
    assert "sample text document" in docs[0].page_content.lower()


def test_load_pdf() -> None:
    loader = XbergLoader(file_path=FIXTURES / "sample.pdf")
    docs = loader.load()

    _assert_valid_documents(docs, expected_source="sample.pdf")


def test_load_docx() -> None:
    loader = XbergLoader(file_path=FIXTURES / "sample.docx")
    docs = loader.load()

    _assert_valid_documents(docs, expected_source="sample.docx")


def test_load_html() -> None:
    loader = XbergLoader(file_path=FIXTURES / "sample.html")
    docs = loader.load()

    _assert_valid_documents(docs, expected_source="sample.html")
    assert "sample" in docs[0].page_content.lower()


def test_load_bytes() -> None:
    data = b"Hello from bytes extraction test."
    loader = XbergLoader(data=data, mime_type="text/plain")
    docs = loader.load()

    _assert_valid_documents(docs, expected_source="bytes://")
    assert "Hello" in docs[0].page_content


def test_load_directory() -> None:
    loader = XbergLoader(file_path=FIXTURES)
    docs = loader.load()

    _assert_valid_documents(docs, min_count=4)


def test_load_multiple_file_paths() -> None:
    paths: list[str | Path] = [FIXTURES / "sample.txt", FIXTURES / "sample.html"]
    loader = XbergLoader(file_path=paths)
    docs = loader.load()

    _assert_valid_documents(docs, min_count=2)
    sources = [doc.metadata["source"] for doc in docs]
    assert any("sample.txt" in s for s in sources)
    assert any("sample.html" in s for s in sources)


def test_load_with_chunking() -> None:
    config = ExtractionConfig(chunking=ChunkingConfig(max_characters=200, overlap=20))
    loader = XbergLoader(file_path=FIXTURES / "sample.pdf", config=config)
    docs = loader.load()

    _assert_valid_documents(docs, min_count=2, expected_source="sample.pdf")
    total = docs[0].metadata["total_chunks"]
    assert len(docs) == total
    for index, doc in enumerate(docs):
        assert doc.metadata["chunk_index"] == index
        assert "chunk_type" in doc.metadata


def test_load_with_page_splitting() -> None:
    config = ExtractionConfig(pages=PageConfig(extract_pages=True))
    loader = XbergLoader(file_path=FIXTURES / "sample.pdf", config=config)
    docs = loader.load()

    _assert_valid_documents(docs, expected_source="sample.pdf")
    assert all("page" in doc.metadata for doc in docs)


def test_lazy_load_yields_documents() -> None:
    loader = XbergLoader(file_path=FIXTURES / "sample.txt")
    result = loader.lazy_load()

    assert hasattr(result, "__next__")
    docs = list(result)
    _assert_valid_documents(docs)


async def test_async_load_single_file() -> None:
    loader = XbergLoader(file_path=FIXTURES / "sample.txt")
    docs = await loader.aload()

    _assert_valid_documents(docs, expected_source="sample.txt")


async def test_async_load_directory() -> None:
    loader = XbergLoader(file_path=FIXTURES)
    docs: list[Document] = []
    async for doc in loader.alazy_load():
        docs.append(doc)

    _assert_valid_documents(docs, min_count=4)


def test_nonexistent_file_raises() -> None:
    loader = XbergLoader(file_path="/tmp/does_not_exist_xberg_test.pdf")  # noqa: S108

    with pytest.raises((XbergError, OSError), match="does_not_exist"):
        loader.load()
