"""Tests for XbergReader."""

import base64
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import pytest
from llama_index.core.readers.base import BasePydanticReader
from llama_index.readers.xberg import XbergReader
from llama_index.readers.xberg._utils import build_metadata, generate_doc_id

from tests.conftest import (
    make_chunk,
    make_document,
    make_element,
    make_error,
    make_page_content,
    make_result,
)


def test_class() -> None:
    names_of_base_classes = [b.__name__ for b in XbergReader.__mro__]
    assert BasePydanticReader.__name__ in names_of_base_classes


def test_class_name() -> None:
    assert XbergReader().class_name() == "XbergReader"


def test_is_remote_false() -> None:
    assert XbergReader().is_remote is False


def test_default_fields() -> None:
    reader = XbergReader()
    assert reader.raise_on_error is False
    assert reader.extraction_config is None


# --- Config handling (ExtractionConfig is a plain dict / TypedDict) --- ~keep


def test_to_dict_without_config() -> None:
    d = XbergReader().to_dict()
    assert d["extraction_config"] is None
    assert d["raise_on_error"] is False


def test_to_dict_with_config() -> None:
    reader = XbergReader(extraction_config={"output_format": "markdown", "force_ocr": True})
    d = reader.to_dict()
    assert isinstance(d["extraction_config"], dict)
    assert d["extraction_config"]["output_format"] == "markdown"
    assert d["extraction_config"]["force_ocr"] is True


def test_from_dict_round_trip() -> None:
    reader = XbergReader(
        extraction_config={"output_format": "markdown", "pages": {"extract_pages": True}},
        raise_on_error=True,
    )
    restored = XbergReader.from_dict(reader.to_dict())
    assert restored.raise_on_error is True
    assert restored.extraction_config == {"output_format": "markdown", "pages": {"extract_pages": True}}


def test_accepts_dict_as_extraction_config() -> None:
    reader = XbergReader(extraction_config={"output_format": "markdown", "force_ocr": True})
    assert reader.extraction_config == {"output_format": "markdown", "force_ocr": True}


def test_rejects_invalid_extraction_config() -> None:
    with pytest.raises(ValueError, match="Expected ExtractionConfig"):
        XbergReader(extraction_config=42)  # type: ignore[arg-type]


def test_build_config_defaults_result_format() -> None:
    assert XbergReader()._build_config() == {"result_format": "element_based"}


def test_build_config_respects_user_result_format() -> None:
    reader = XbergReader(extraction_config={"result_format": "unified"})
    assert reader._build_config()["result_format"] == "unified"


def test_build_config_page_request_defaults_to_unified() -> None:
    # Opting into page extraction switches the default away from element_based
    # so pages split cleanly without replicating the document-wide elements. ~keep
    reader = XbergReader(extraction_config={"pages": {"extract_pages": True}})
    assert reader._build_config()["result_format"] == "unified"


def test_build_config_explicit_format_wins_over_page_request() -> None:
    reader = XbergReader(extraction_config={"pages": {"extract_pages": True}, "result_format": "element_based"})
    assert reader._build_config()["result_format"] == "element_based"


def test_standard_metadata_fields() -> None:
    document = make_document(page_count=5)
    meta = build_metadata(document=document, file_path=Path("/tmp/test.pdf"))
    assert meta["file_name"] == "test.pdf"
    assert meta["file_path"] == "/tmp/test.pdf"
    assert meta["file_type"] == "application/pdf"
    assert meta["total_pages"] == 5


def test_document_metadata_fields() -> None:
    meta = build_metadata(document=make_document(), file_path=Path("/tmp/test.pdf"))
    assert meta["title"] == "Test Document"
    assert meta["authors"] == ["Author One"]
    assert meta["language"] == "eng"
    assert meta["output_format"] == "plain"


def test_extraction_result_fields() -> None:
    meta = build_metadata(document=make_document(quality_score=0.88), file_path=Path("/tmp/test.pdf"))
    assert meta["quality_score"] == 0.88
    assert meta["detected_languages"] == ["eng"]


def test_extra_info_overrides() -> None:
    meta = build_metadata(
        document=make_document(),
        file_path=Path("/tmp/test.pdf"),
        extra_info={"title": "Override", "custom": "value"},
    )
    assert meta["title"] == "Override"
    assert meta["custom"] == "value"


def test_bytes_source_metadata() -> None:
    meta = build_metadata(document=make_document(), source="bytes_input")
    assert meta["file_name"] == "bytes_input"
    assert meta["file_path"] == "bytes_input"


def test_keywords_and_warnings_serialized() -> None:
    document = make_document(
        extracted_keywords=[SimpleNamespace(text="rust", score=0.9, algorithm="yake")],
        processing_warnings=[SimpleNamespace(source="pdf", message="slow")],
    )
    meta = build_metadata(document=document, file_path=Path("/tmp/t.pdf"))
    assert meta["extracted_keywords"] == [{"text": "rust", "score": 0.9, "algorithm": "yake"}]
    assert meta["processing_warnings"] == [{"source": "pdf", "message": "slow"}]


def test_file_path_id_deterministic() -> None:
    path = Path("/tmp/test.pdf")
    assert generate_doc_id(file_path=path) == generate_doc_id(file_path=path)


def test_file_path_id_with_page() -> None:
    path = Path("/tmp/test.pdf")
    assert generate_doc_id(file_path=path) != generate_doc_id(file_path=path, page_number=1)
    assert generate_doc_id(file_path=path, page_number=1) != generate_doc_id(file_path=path, page_number=2)


def test_bytes_id_deterministic() -> None:
    assert generate_doc_id(data=b"hello") == generate_doc_id(data=b"hello")


def test_bytes_id_with_page() -> None:
    assert generate_doc_id(data=b"hello") != generate_doc_id(data=b"hello", page_number=1)


def test_different_paths_different_ids() -> None:
    assert generate_doc_id(file_path=Path("/tmp/a.pdf")) != generate_doc_id(file_path=Path("/tmp/b.pdf"))


def test_generate_doc_id_no_input_raises() -> None:
    with pytest.raises(ValueError, match="Either file_path or data must be provided"):
        generate_doc_id()


def test_prepare_single_file() -> None:
    task = XbergReader._prepare_extractions(file_path=Path("/tmp/test.pdf"))
    assert task.kind == "file"
    assert task.paths == (Path("/tmp/test.pdf"),)


def test_prepare_single_file_from_string() -> None:
    task = XbergReader._prepare_extractions(file_path="/tmp/test.pdf")
    assert task.kind == "file"


def test_prepare_single_element_list_routes_to_single() -> None:
    task = XbergReader._prepare_extractions(file_path=[Path("/tmp/test.pdf")])
    assert task.kind == "file"


def test_prepare_batch_files() -> None:
    task = XbergReader._prepare_extractions(file_path=[Path("/tmp/a.pdf"), Path("/tmp/b.pdf")])
    assert task.kind == "file_batch"
    assert task.paths == (Path("/tmp/a.pdf"), Path("/tmp/b.pdf"))


def test_prepare_single_bytes() -> None:
    task = XbergReader._prepare_extractions(data=b"pdf", mime_type="application/pdf")
    assert task.kind == "bytes"
    assert task.data_list == (b"pdf",)
    assert task.mime_types == ("application/pdf",)


def test_prepare_batch_bytes() -> None:
    task = XbergReader._prepare_extractions(data=[b"a", b"b"], mime_type=["application/pdf", "text/plain"])
    assert task.kind == "bytes_batch"


def test_prepare_no_input_raises() -> None:
    with pytest.raises(ValueError, match="Either file_path or data"):
        XbergReader._prepare_extractions()


def test_prepare_bytes_without_mime_raises() -> None:
    with pytest.raises(ValueError, match="mime_type must be a string"):
        XbergReader._prepare_extractions(data=b"pdf")


def test_prepare_bytes_with_list_mime_raises() -> None:
    with pytest.raises(ValueError, match="mime_type must be a string"):
        XbergReader._prepare_extractions(data=b"pdf", mime_type=["application/pdf"])


def test_prepare_batch_bytes_length_mismatch_raises() -> None:
    with pytest.raises(ValueError, match="parallel lists of equal length"):
        XbergReader._prepare_extractions(data=[b"a", b"b"], mime_type=["application/pdf"])


def test_build_inputs_file_uses_uri() -> None:
    task = XbergReader._prepare_extractions(file_path=Path("/tmp/test.pdf"))
    inputs, sources = XbergReader._build_inputs(task)
    assert inputs[0].kind == "uri"
    assert inputs[0].uri == "/tmp/test.pdf"
    assert sources[0].path == Path("/tmp/test.pdf")


def test_build_inputs_bytes_uses_bytes() -> None:
    task = XbergReader._prepare_extractions(data=b"pdf", mime_type="application/pdf")
    inputs, sources = XbergReader._build_inputs(task)
    assert inputs[0].kind == "bytes"
    assert inputs[0].bytes == b"pdf"
    assert inputs[0].mime_type == "application/pdf"
    assert sources[0].data == b"pdf"


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_single_file_returns_document(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document(content="Hello PDF")])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert len(docs) == 1
    assert docs[0].text == "Hello PDF"
    mock_extract.assert_awaited_once()


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_single_file_metadata(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document()])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    meta = docs[0].metadata
    assert meta["file_name"] == "test.pdf"
    assert meta["file_type"] == "application/pdf"
    assert meta["title"] == "Test Document"


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_single_file_deterministic_id(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document()])
    reader = XbergReader()
    assert reader.load_data(Path("/tmp/test.pdf"))[0].id_ == reader.load_data(Path("/tmp/test.pdf"))[0].id_


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_extra_info_merged(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document()])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"), extra_info={"custom": "value"})
    assert docs[0].metadata["custom"] == "value"


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_string_path_accepted(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document()])
    assert len(XbergReader().load_data("/tmp/test.pdf")) == 1


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_extraction_config_passed(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document()])
    XbergReader(extraction_config={"output_format": "markdown"}).load_data(Path("/tmp/test.pdf"))
    _, config = mock_extract.await_args.args
    assert config["output_format"] == "markdown"
    assert config["result_format"] == "element_based"


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_single_bytes(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document(content="Bytes content")])
    docs = XbergReader().load_data(data=b"pdf bytes", mime_type="application/pdf")
    assert docs[0].text == "Bytes content"


@patch("llama_index.readers.xberg.base.extract_batch", new_callable=AsyncMock)
def test_batch_files(mock_batch: AsyncMock) -> None:
    mock_batch.return_value = make_result([make_document(content="Doc A"), make_document(content="Doc B")])
    docs = XbergReader().load_data([Path("/tmp/a.pdf"), Path("/tmp/b.pdf")])
    assert [d.text for d in docs] == ["Doc A", "Doc B"]
    mock_batch.assert_awaited_once()


@patch("llama_index.readers.xberg.base.extract_batch", new_callable=AsyncMock)
def test_batch_unique_ids(mock_batch: AsyncMock) -> None:
    mock_batch.return_value = make_result([make_document(), make_document()])
    docs = XbergReader().load_data([Path("/tmp/a.pdf"), Path("/tmp/b.pdf")])
    assert docs[0].id_ != docs[1].id_


@patch("llama_index.readers.xberg.base.extract_batch", new_callable=AsyncMock)
def test_batch_bytes(mock_batch: AsyncMock) -> None:
    mock_batch.return_value = make_result([make_document(content="A"), make_document(content="B")])
    docs = XbergReader().load_data(data=[b"b1", b"b2"], mime_type=["application/pdf", "application/pdf"])
    assert len(docs) == 2


@patch("llama_index.readers.xberg.base.extract_batch", new_callable=AsyncMock)
def test_batch_partial_error_maps_survivors(mock_batch: AsyncMock) -> None:
    # Input index 1 failed; the single surviving document maps to the first source. ~keep
    mock_batch.return_value = make_result([make_document(content="A")], errors=[make_error(index=1)])
    docs = XbergReader().load_data([Path("/tmp/a.pdf"), Path("/tmp/bad.pdf")])
    assert len(docs) == 1
    assert docs[0].text == "A"
    assert docs[0].metadata["file_name"] == "a.pdf"


def test_bytes_without_mime_type_raises() -> None:
    with pytest.raises(ValueError, match="mime_type must be a string"):
        XbergReader().load_data(data=b"bytes")


def test_batch_bytes_length_mismatch_raises() -> None:
    with pytest.raises(ValueError, match="parallel lists of equal length"):
        XbergReader().load_data(data=[b"a", b"b"], mime_type=["application/pdf"])


def test_no_input_raises() -> None:
    with pytest.raises(ValueError, match="Either file_path or data"):
        XbergReader().load_data()


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_per_page_yields_multiple_documents(mock_extract: AsyncMock) -> None:
    document = make_document(
        page_count=3,
        pages=[
            make_page_content(page_number=1, content="Page 1"),
            make_page_content(page_number=2, content="Page 2"),
            make_page_content(page_number=3, content="Page 3"),
        ],
    )
    mock_extract.return_value = make_result([document])
    docs = XbergReader(extraction_config={"pages": {"extract_pages": True}}).load_data(Path("/tmp/test.pdf"))
    assert [d.text for d in docs] == ["Page 1", "Page 2", "Page 3"]


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_per_page_metadata_has_page_number(mock_extract: AsyncMock) -> None:
    document = make_document(
        page_count=2,
        pages=[make_page_content(page_number=1, content="P1"), make_page_content(page_number=2, content="P2")],
    )
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert docs[0].metadata["page_number"] == 1
    assert docs[1].metadata["page_number"] == 2


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_per_page_unique_ids(mock_extract: AsyncMock) -> None:
    document = make_document(
        page_count=2,
        pages=[make_page_content(page_number=1, content="P1"), make_page_content(page_number=2, content="P2")],
    )
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert docs[0].id_ != docs[1].id_


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_tolerant_mode_skips_hard_error(mock_extract: AsyncMock) -> None:
    mock_extract.side_effect = RuntimeError("extraction failed")
    assert XbergReader(raise_on_error=False).load_data(Path("/tmp/bad.pdf")) == []


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_strict_mode_raises_hard_error(mock_extract: AsyncMock) -> None:
    mock_extract.side_effect = RuntimeError("extraction failed")
    with pytest.raises(RuntimeError, match="extraction failed"):
        XbergReader(raise_on_error=True).load_data(Path("/tmp/bad.pdf"))


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_tolerant_mode_skips_soft_error(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([], errors=[make_error(index=0, message="bad file")])
    assert XbergReader(raise_on_error=False).load_data(Path("/tmp/bad.pdf")) == []


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_strict_mode_raises_soft_error(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([], errors=[make_error(index=0, message="bad file")])
    with pytest.raises(RuntimeError, match="bad file"):
        XbergReader(raise_on_error=True).load_data(Path("/tmp/bad.pdf"))


# --- Element contract (reader emits JSON-serialisable dicts) --- ~keep


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_elements_serialized_to_dicts(mock_extract: AsyncMock) -> None:
    document = make_document(
        elements=[make_element(element_type="title", text="Hello", page_number=1, element_index=0)]
    )
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert docs[0].metadata["_xberg_elements"] == [
        {"text": "Hello", "element_type": "title", "metadata": {"page_number": 1, "element_index": 0}}
    ]


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_elements_excluded_from_llm_keys(mock_extract: AsyncMock) -> None:
    document = make_document(elements=[make_element(text="Hello")])
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert "_xberg_elements" in docs[0].excluded_llm_metadata_keys
    assert "_xberg_elements" in docs[0].excluded_embed_metadata_keys


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_no_elements_when_none(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document(elements=None)])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert "_xberg_elements" not in docs[0].metadata
    assert docs[0].excluded_llm_metadata_keys == []


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_chunks_serialized_to_dicts(mock_extract: AsyncMock) -> None:
    document = make_document(
        elements=None,
        chunks=[
            make_chunk(
                content="Chunk one.",
                chunk_type="heading",
                chunk_index=0,
                total_chunks=1,
                heading_path=["Intro"],
                token_count=3,
            )
        ],
    )
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert docs[0].metadata["_xberg_chunks"] == [
        {
            "content": "Chunk one.",
            "chunk_type": "heading",
            "metadata": {
                "chunk_index": 0,
                "total_chunks": 1,
                "first_page": 1,
                "last_page": 1,
                "heading_path": ["Intro"],
                "token_count": 3,
            },
        }
    ]


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_chunks_excluded_from_llm_and_embed_keys(mock_extract: AsyncMock) -> None:
    document = make_document(elements=None, chunks=[make_chunk()])
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert "_xberg_chunks" in docs[0].excluded_llm_metadata_keys
    assert "_xberg_chunks" in docs[0].excluded_embed_metadata_keys


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_chunks_suppress_per_page_splitting(mock_extract: AsyncMock) -> None:
    # Chunks are document-global; a chunk-bearing doc must stay a single Document
    # even when pages are present, rather than replicating chunks onto each page. ~keep
    document = make_document(
        page_count=2,
        pages=[make_page_content(page_number=1, content="P1"), make_page_content(page_number=2, content="P2")],
        chunks=[make_chunk()],
    )
    mock_extract.return_value = make_result([document])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert len(docs) == 1
    assert "_xberg_chunks" in docs[0].metadata


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_no_chunks_when_none(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document(chunks=None)])
    docs = XbergReader().load_data(Path("/tmp/test.pdf"))
    assert "_xberg_chunks" not in docs[0].metadata


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_images_base64_encoded(mock_extract: AsyncMock) -> None:
    image = SimpleNamespace(
        data=b"\x89PNG\r\n",
        format="PNG",
        image_index=0,
        page_number=1,
        width=100,
        height=200,
        colorspace="RGB",
        bits_per_component=8,
        is_mask=False,
        description="test image",
        bounding_box=None,
        ocr_result=None,
    )
    mock_extract.return_value = make_result([make_document(images=[image])])
    images = XbergReader().load_data(Path("/tmp/test.pdf"))[0].metadata["images"]
    assert len(images) == 1
    assert images[0]["data"] == base64.b64encode(b"\x89PNG\r\n").decode("ascii")
    assert images[0]["format"] == "PNG"
    assert images[0]["width"] == 100


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_table_appended_when_not_inlined(mock_extract: AsyncMock) -> None:
    table = SimpleNamespace(markdown="| A | B |\n|---|---|\n| 1 | 2 |")
    mock_extract.return_value = make_result([make_document(content="Main text", tables=[table])])
    text = XbergReader().load_data(Path("/tmp/test.pdf"))[0].text
    assert "| A | B |" in text
    assert "Main text" in text


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
def test_table_not_duplicated_when_inlined(mock_extract: AsyncMock) -> None:
    table_md = "| A | B |\n|---|---|\n| 1 | 2 |"
    table = SimpleNamespace(markdown=table_md)
    document = make_document(content=f"Text before\n\n{table_md}\n\nText after", tables=[table])
    mock_extract.return_value = make_result([document])
    assert XbergReader().load_data(Path("/tmp/test.pdf"))[0].text.count("| A | B |") == 1


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
async def test_aload_data_single_file(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document(content="Async content")])
    docs = await XbergReader().aload_data(Path("/tmp/test.pdf"))
    assert docs[0].text == "Async content"


@patch("llama_index.readers.xberg.base.extract_batch", new_callable=AsyncMock)
async def test_aload_data_batch(mock_batch: AsyncMock) -> None:
    mock_batch.return_value = make_result([make_document(content="A"), make_document(content="B")])
    docs = await XbergReader().aload_data([Path("/tmp/a.pdf"), Path("/tmp/b.pdf")])
    assert len(docs) == 2


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
async def test_aload_data_bytes(mock_extract: AsyncMock) -> None:
    mock_extract.return_value = make_result([make_document(content="Async bytes")])
    docs = await XbergReader().aload_data(data=b"pdf", mime_type="application/pdf")
    assert docs[0].text == "Async bytes"


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
async def test_async_error_tolerant(mock_extract: AsyncMock) -> None:
    mock_extract.side_effect = RuntimeError("async fail")
    assert await XbergReader(raise_on_error=False).aload_data(Path("/tmp/bad.pdf")) == []


@patch("llama_index.readers.xberg.base.extract", new_callable=AsyncMock)
async def test_async_error_strict(mock_extract: AsyncMock) -> None:
    mock_extract.side_effect = RuntimeError("async fail")
    with pytest.raises(RuntimeError, match="async fail"):
        await XbergReader(raise_on_error=True).aload_data(Path("/tmp/bad.pdf"))
