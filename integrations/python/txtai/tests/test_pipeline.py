"""Tests for txtai_xberg.pipeline.XbergPipeline."""

import asyncio
from pathlib import Path

import pytest
from xberg import ChunkingConfig, ExtractionConfig

from txtai_xberg import ExtractionFailedError, XbergPipeline


@pytest.fixture
def pipeline() -> XbergPipeline:
    return XbergPipeline()


def test_single_path_returns_single_document(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = pipeline(str(sample_html_path))

    assert isinstance(doc, dict)
    assert set(doc.keys()) == {"content", "metadata"}


def test_single_path_content_is_non_empty_string(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = pipeline(str(sample_html_path))

    assert isinstance(doc["content"], str)
    assert "Sample Document" in doc["content"]


def test_batch_input_preserves_order_and_length(
    pipeline: XbergPipeline,
    sample_html_path: Path,
    sample_pdf_path: Path,
) -> None:
    paths = [str(sample_html_path), str(sample_pdf_path)]

    docs = pipeline(paths)

    assert isinstance(docs, list)
    assert len(docs) == 2
    assert docs[0]["metadata"]["source"] == paths[0]
    assert docs[1]["metadata"]["source"] == paths[1]


def test_single_element_list_returns_list(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    docs = pipeline([str(sample_html_path)])

    assert isinstance(docs, list)
    assert len(docs) == 1
    assert docs[0]["metadata"]["source"] == str(sample_html_path)


def test_empty_list_returns_empty_list(pipeline: XbergPipeline) -> None:
    docs = pipeline([])

    assert docs == []


def test_metadata_source_matches_input_path(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    path = str(sample_html_path)

    doc = pipeline(path)

    assert doc["metadata"]["source"] == path


def test_metadata_mime_type_is_populated_for_html(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = pipeline(str(sample_html_path))

    mime = doc["metadata"]["mime_type"]
    assert mime is not None
    assert "html" in mime.lower()


def test_metadata_has_stable_keys(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = pipeline(str(sample_html_path))

    expected_keys = {
        "source",
        "mime_type",
        "title",
        "authors",
        "languages",
        "page_count",
    }
    assert set(doc["metadata"].keys()) == expected_keys


def test_pdf_page_count_matches_fixture(sample_pdf_path: Path) -> None:
    # Xberg reports per-page counts on the PDF markdown path, where the
    # three-page fixture is split page by page. ~keep
    pipeline = XbergPipeline(config=ExtractionConfig(output_format="markdown"))

    doc = pipeline(str(sample_pdf_path))

    assert doc["metadata"]["page_count"] == 3


def test_pdf_title_is_none(pipeline: XbergPipeline, sample_pdf_path: Path) -> None:
    doc = pipeline(str(sample_pdf_path))

    assert doc["metadata"]["title"] is None


def test_pdf_content_contains_fixture_text(pipeline: XbergPipeline, sample_pdf_path: Path) -> None:
    doc = pipeline(str(sample_pdf_path))

    assert "Sample PDF" in doc["content"]


def test_pdf_mime_type_is_application_pdf(pipeline: XbergPipeline, sample_pdf_path: Path) -> None:
    doc = pipeline(str(sample_pdf_path))

    assert doc["metadata"]["mime_type"] == "application/pdf"


def test_docx_extracts_content(pipeline: XbergPipeline, sample_docx_path: Path) -> None:
    doc = pipeline(str(sample_docx_path))

    assert "DOCX" in doc["content"]
    mime = doc["metadata"]["mime_type"]
    assert mime is not None
    assert "wordprocessingml" in mime or "officedocument" in mime


def test_docx_title_is_populated(pipeline: XbergPipeline, sample_docx_path: Path) -> None:
    doc = pipeline(str(sample_docx_path))

    assert doc["metadata"]["title"] == "DOCX Demo"


def test_docx_page_count_from_counts(pipeline: XbergPipeline, sample_docx_path: Path) -> None:
    doc = pipeline(str(sample_docx_path))

    assert doc["metadata"]["page_count"] == 1


def test_html_extracts_content_with_title(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = pipeline(str(sample_html_path))

    assert "Sample Document" in doc["content"]
    assert doc["metadata"]["title"] == "Sample HTML Document"


def test_txt_extracts_plain_content(pipeline: XbergPipeline, sample_txt_path: Path) -> None:
    doc = pipeline(str(sample_txt_path))

    content = doc["content"]
    assert isinstance(content, str)
    assert len(content) > 0
    assert doc["metadata"]["mime_type"] == "text/plain"
    assert doc["metadata"]["title"] is None
    assert doc["metadata"]["page_count"] == 0


def test_default_constructor_leaves_config_none() -> None:
    pipe = XbergPipeline()

    assert pipe._config is None


def test_config_is_stored_verbatim() -> None:
    override = ExtractionConfig(output_format="plain")
    pipe = XbergPipeline(config=override)

    assert pipe._config is override


def test_config_drives_extraction_output_format(sample_html_path: Path) -> None:
    plain = XbergPipeline(config=ExtractionConfig(output_format="plain"))
    markdown = XbergPipeline(config=ExtractionConfig(output_format="markdown"))

    plain_content = plain(str(sample_html_path))["content"]
    markdown_content = markdown(str(sample_html_path))["content"]

    assert plain_content != markdown_content


def test_missing_file_raises_with_error_index(
    pipeline: XbergPipeline,
    sample_html_path: Path,
    tmp_path: Path,
) -> None:
    missing = tmp_path / "does_not_exist.pdf"
    paths = [str(sample_html_path), str(missing)]

    with pytest.raises(ExtractionFailedError) as exc_info:
        pipeline(paths)

    errors = exc_info.value.errors
    assert len(errors) == 1
    assert errors[0].index == 1
    assert errors[0].source == str(missing)


def test_acall_returns_single_document(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = asyncio.run(pipeline.acall(str(sample_html_path)))

    assert isinstance(doc, dict)
    assert doc["metadata"]["title"] == "Sample HTML Document"


def test_acall_returns_list_for_list_input(
    pipeline: XbergPipeline,
    sample_html_path: Path,
    sample_txt_path: Path,
) -> None:
    docs = asyncio.run(pipeline.acall([str(sample_html_path), str(sample_txt_path)]))

    assert isinstance(docs, list)
    assert len(docs) == 2


def test_metadata_authors_and_languages_present_for_html(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    doc = pipeline(str(sample_html_path))

    # Keys always present; values may be None when the document carries no such data. ~keep
    assert "authors" in doc["metadata"]
    assert "languages" in doc["metadata"]


def test_to_documents_without_chunking_yields_one_document_per_file(
    pipeline: XbergPipeline,
    sample_html_path: Path,
    sample_txt_path: Path,
) -> None:
    paths = [str(sample_html_path), str(sample_txt_path)]

    docs = pipeline.to_documents(paths)

    assert len(docs) == 2
    ids = [doc_id for doc_id, _text, _tags in docs]
    assert ids == paths
    for _doc_id, text, tags in docs:
        assert isinstance(text, str)
        assert tags["source"] in paths
        assert set(tags.keys()) == {"source", "mime_type", "title", "page_count"}


def test_to_documents_single_string_returns_flat_list(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    docs = pipeline.to_documents(str(sample_html_path))

    assert isinstance(docs, list)
    assert len(docs) == 1
    assert docs[0][0] == str(sample_html_path)
    assert "Sample Document" in docs[0][1]


def test_to_documents_with_chunking_splits_into_multiple_segments(
    sample_pdf_path: Path,
) -> None:
    chunked = XbergPipeline(config=ExtractionConfig(chunking=ChunkingConfig(max_characters=200, overlap=20)))

    docs = chunked.to_documents(str(sample_pdf_path))

    # The three-page fixture yields many small chunks at 200 chars each. ~keep
    assert len(docs) > 1
    first_id, first_text, first_tags = docs[0]
    assert first_id == f"{sample_pdf_path}#0"
    assert len(first_text) > 0
    assert first_tags["chunk_index"] == 0
    assert first_tags["total_chunks"] == len(docs)
    assert first_tags["source"] == str(sample_pdf_path)
    assert set(first_tags.keys()) == {
        "source",
        "mime_type",
        "title",
        "chunk_index",
        "total_chunks",
        "heading_path",
        "first_page",
        "last_page",
        "token_count",
    }


def test_to_documents_chunk_ids_are_unique(sample_pdf_path: Path) -> None:
    chunked = XbergPipeline(config=ExtractionConfig(chunking=ChunkingConfig(max_characters=200, overlap=20)))

    docs = chunked.to_documents(str(sample_pdf_path))

    ids = [doc_id for doc_id, _text, _tags in docs]
    assert len(ids) == len(set(ids))


def test_to_documents_raises_on_missing_file(pipeline: XbergPipeline, tmp_path: Path) -> None:
    missing = tmp_path / "nope.pdf"

    with pytest.raises(ExtractionFailedError):
        pipeline.to_documents([str(missing)])


def test_ato_documents_matches_sync(pipeline: XbergPipeline, sample_html_path: Path) -> None:
    sync_docs = pipeline.to_documents(str(sample_html_path))
    async_docs = asyncio.run(pipeline.ato_documents(str(sample_html_path)))

    assert sync_docs == async_docs
