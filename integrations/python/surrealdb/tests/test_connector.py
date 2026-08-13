"""Tests for DocumentConnector."""

import hashlib
from unittest.mock import AsyncMock, MagicMock, patch

import pytest
import anyio
from surrealdb.errors import ServerError
from xberg import ExtractionConfig, ExtractionResult

from tests.conftest import make_document
from surrealdb_xberg.connector import DocumentConnector
from surrealdb_xberg.exceptions import DimensionMismatchError, IngestionError, SchemaNotInitializedError

_INPUT = "surrealdb_xberg._base._input_from_path"
_EXTRACT = "surrealdb_xberg._base.extract"
_EXTRACT_BATCH = "surrealdb_xberg._base.extract_batch"


def test_analyzer_name(mock_client: AsyncMock) -> None:
    connector = DocumentConnector(db=mock_client)
    assert connector.analyzer_name == "doc_analyzer"


def test_client_property(mock_client: AsyncMock) -> None:
    connector = DocumentConnector(db=mock_client)
    assert connector.client is mock_client


def test_table_property(mock_client: AsyncMock) -> None:
    connector = DocumentConnector(db=mock_client, table="my_docs")
    assert connector.table == "my_docs"


@patch("surrealdb_xberg.connector.build_connector_schema")
async def test_connector_setup_schema_forwards_params(
    mock_build: MagicMock,
    mock_client: AsyncMock,
) -> None:
    """setup_schema() passes all parameters to build_connector_schema and executes every statement."""
    mock_build.return_value = ["STMT1;", "STMT2;"]
    connector = DocumentConnector(db=mock_client)

    await connector.setup_schema(
        analyzer_language="german",
        bm25_k1=1.5,
        bm25_b=0.8,
    )

    mock_build.assert_called_once_with(
        table="documents",
        analyzer_language="german",
        bm25_k1=1.5,
        bm25_b=0.8,
    )
    assert mock_client.query.call_count == 2
    mock_client.query.assert_any_call("STMT1;")
    mock_client.query.assert_any_call("STMT2;")


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_ingest_file(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    mock_extract.return_value = sample_result

    await connector.ingest_file("/tmp/test.pdf")

    mock_extract.assert_awaited_once()
    mock_client.query.assert_called_once()
    call_args = mock_client.query.call_args
    assert "INSERT IGNORE INTO documents" in call_args[0][0]
    records = call_args[0][1]["records"]
    assert records[0]["source"] == "/tmp/test.pdf"
    assert records[0]["content"] == sample_result.results[0].content


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_ingest_file_passes_custom_config(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    mock_extract.return_value = sample_result
    user_config = ExtractionConfig()

    connector = DocumentConnector(db=mock_client, config=user_config)
    await connector.setup_schema()
    mock_client.query.reset_mock()

    await connector.ingest_file("/tmp/test.pdf")

    # extract(input, config) — config is the second positional argument. ~keep
    assert mock_extract.call_args.args[1] is user_config


@patch(_EXTRACT, new_callable=AsyncMock)
async def test_ingest_bytes(
    mock_extract: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    mock_extract.return_value = sample_result

    await connector.ingest_bytes(data=b"hello world", mime_type="text/plain", source="api://response")

    mock_extract.assert_awaited_once()
    records = mock_client.query.call_args[0][1]["records"]
    assert records[0]["source"] == "api://response"


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_content_hash_computed(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    mock_extract.return_value = sample_result

    await connector.ingest_file("/tmp/test.txt")

    records = mock_client.query.call_args[0][1]["records"]
    expected_hash = hashlib.sha256(sample_result.results[0].content.encode()).hexdigest()
    assert records[0]["content_hash"] == expected_hash


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_metadata_fields_mapped(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    mock_extract.return_value = sample_result

    await connector.ingest_file("/tmp/test.txt")

    records = mock_client.query.call_args[0][1]["records"]
    doc = records[0]
    assert doc["title"] == "Test Document"
    assert doc["authors"] == "Alice, Bob"
    assert doc["quality_score"] == 0.95
    assert doc["detected_languages"] == ["en"]
    assert doc["keywords"] == ["test"]


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT_BATCH, new_callable=AsyncMock)
async def test_connector_ingest_files_batches_extraction_and_insert(
    mock_extract_batch: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
) -> None:
    """ingest_files() extracts every path in one extract_batch and inserts rows together."""
    documents = [make_document(content=f"doc {i}") for i in range(3)]
    mock_extract_batch.return_value = ExtractionResult(results=documents)

    await connector.ingest_files(["/tmp/a.txt", "/tmp/b.txt", "/tmp/c.txt"])

    mock_extract_batch.assert_awaited_once()
    # A single extract_batch call, and a single batched INSERT for all three rows. ~keep
    mock_client.query.assert_called_once()
    records = mock_client.query.call_args[0][1]["records"]
    assert len(records) == 3
    assert {r["source"] for r in records} == {"/tmp/a.txt", "/tmp/b.txt", "/tmp/c.txt"}


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_connector_raises_on_silent_insert_error(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    """Defensive fallback: an error string in the result list still raises."""
    mock_extract.return_value = sample_result
    mock_client.query = AsyncMock(return_value=["Some unexpected database error"])

    with pytest.raises(IngestionError, match="INSERT IGNORE failed silently"):
        await connector.ingest_file("/tmp/test.pdf")


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_connector_wraps_server_error_as_ingestion_error(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    """SurrealDB 2.0 raises ServerError on status ERR — it is re-raised as IngestionError."""
    mock_extract.return_value = sample_result
    mock_client.query = AsyncMock(side_effect=ServerError("Query", "record already exists"))

    with pytest.raises(IngestionError, match="record already exists"):
        await connector.ingest_file("/tmp/test.pdf")


@patch(_INPUT, new_callable=AsyncMock)
@patch(_EXTRACT, new_callable=AsyncMock)
async def test_connector_wraps_dimension_server_error(
    mock_extract: AsyncMock,
    _mock_input: AsyncMock,
    connector: DocumentConnector,
    mock_client: AsyncMock,
    sample_result: ExtractionResult,
) -> None:
    mock_extract.return_value = sample_result
    mock_client.query = AsyncMock(
        side_effect=ServerError("Query", "Incorrect vector dimension (384). Expected 768."),
    )

    with pytest.raises(DimensionMismatchError, match="Vector dimension mismatch"):
        await connector.ingest_file("/tmp/test.pdf")


@pytest.mark.parametrize(
    ("method", "args", "kwargs"),
    [
        ("ingest_file", ["/tmp/test.pdf"], {}),
        ("ingest_files", [["/tmp/a.pdf", "/tmp/b.pdf"]], {}),
        ("ingest_directory", ["/tmp"], {}),
        ("ingest_bytes", [], {"data": b"hello", "mime_type": "text/plain", "source": "test"}),
    ],
    ids=["ingest_file", "ingest_files", "ingest_directory", "ingest_bytes"],
)
async def test_connector_raises_without_schema(
    mock_client: AsyncMock,
    method: str,
    args: list[object],
    kwargs: dict[str, object],
) -> None:
    connector = DocumentConnector(db=mock_client)

    with pytest.raises(SchemaNotInitializedError, match="setup_schema"):
        await getattr(connector, method)(*args, **kwargs)


def test_connector_ingest_batch_empty_is_noop(mock_client: AsyncMock) -> None:
    """An empty batch must not issue any query."""
    connector = DocumentConnector(db=mock_client)

    anyio.run(connector._ingest_batch, [])
    mock_client.query.assert_not_called()
