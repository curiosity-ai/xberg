"""Shared test fixtures."""

from pathlib import Path
from unittest.mock import AsyncMock

import pytest

# The result objects returned by ``xberg.extract`` are the native (Rust) classes.
# ``xberg.Metadata`` resolves to the public ``options`` dataclass, which the native
# ``ExtractedDocument`` constructor rejects, so build ``Metadata`` from the native
# module to match the exact shape produced at runtime. ~keep
from xberg import (
    Chunk,
    ChunkMetadata,
    ChunkType,
    ExtractedDocument,
    ExtractionResult,
    Keyword,
    KeywordAlgorithm,
)

# The native ``ExtractedDocument`` constructor rejects the public ``options`` dataclasses
# (``Metadata``, ``Table``, ``Entity``, ``DocumentSummary``), so build these from the native
# module to match the exact shape produced at runtime. ~keep
from xberg._xberg import DocumentSummary, Entity, EntityCategory, Metadata, SummaryStrategy, Table

from surrealdb_xberg import AsyncSurrealQueryable
from surrealdb_xberg.connector import DocumentConnector
from surrealdb_xberg.pipeline import DocumentPipeline

FIXTURES_DIR = Path(__file__).parent / "fixtures"

EMBEDDING_DIMENSIONS = 768


def make_chunk(index: int, *, embedding: list[float] | None = None) -> Chunk:
    """Build a real ``Chunk`` with populated ``ChunkMetadata``."""
    meta = ChunkMetadata(
        byte_start=index * 100,
        byte_end=(index + 1) * 100,
        chunk_index=index,
        total_chunks=3,
        heading_path=[],
        image_indices=[],
        node_ids=[],
        page_spans=[],
        classifications=[],
        token_count=7,
        first_page=index + 1,
        last_page=index + 1,
    )
    return Chunk(
        content=f"Chunk {index} content about testing.",
        chunk_type=ChunkType.UNKNOWN,
        metadata=meta,
        embedding=embedding if embedding is not None else [0.1 * index] * EMBEDDING_DIMENSIONS,
    )


def make_document(
    *,
    content: str = "This is the extracted document content.",
    chunks: list[Chunk] | None = None,
    entities: list[Entity] | None = None,
    tables: list[Table] | None = None,
    summary: str | None = None,
) -> ExtractedDocument:
    """Build a real ``ExtractedDocument`` with typical fields populated."""
    metadata = Metadata(
        title="Test Document",
        authors=["Alice", "Bob"],
        created_at="2024-01-01T00:00:00+00:00",
    )
    return ExtractedDocument(
        content=content,
        mime_type="text/plain",
        metadata=metadata,
        detected_languages=["en"],
        chunks=chunks if chunks is not None else [],
        extracted_keywords=[Keyword(text="test", score=0.9, algorithm=KeywordAlgorithm.YAKE)],
        quality_score=0.95,
        entities=entities,
        tables=tables if tables is not None else [],
        summary=DocumentSummary(text=summary, strategy=SummaryStrategy.ABSTRACTIVE) if summary is not None else None,
    )


def make_entity(category: str, text: str, start: int, end: int, confidence: float | None = 0.9) -> Entity:
    """Build a real ``Entity`` for NER persistence tests."""
    return Entity(category=EntityCategory(category), text=text, start=start, end=end, confidence=confidence)


def make_table(markdown: str, page_number: int, cells: list[list[str]]) -> Table:
    """Build a real ``Table`` for table persistence tests."""
    return Table(markdown=markdown, page_number=page_number, cells=cells)


@pytest.fixture
def mock_client() -> AsyncMock:
    """Mock AsyncSurreal connection."""
    client = AsyncMock(spec=AsyncSurrealQueryable)
    client.query = AsyncMock(return_value=[])
    return client


@pytest.fixture
def sample_document() -> ExtractedDocument:
    """A real ExtractedDocument with typical fields populated and no chunks."""
    return make_document()


@pytest.fixture
def sample_result(sample_document: ExtractedDocument) -> ExtractionResult:
    """An ExtractionResult container wrapping a single document."""
    return ExtractionResult(results=[sample_document])


@pytest.fixture
def sample_chunks() -> list[Chunk]:
    """Three real chunks with embeddings and metadata."""
    return [make_chunk(i) for i in range(3)]


@pytest.fixture
async def connector(mock_client: AsyncMock) -> DocumentConnector:
    """DocumentConnector with schema initialized and mock query reset."""
    conn = DocumentConnector(db=mock_client)
    await conn.setup_schema()
    mock_client.query.reset_mock()
    return conn


@pytest.fixture
async def pipeline(mock_client: AsyncMock) -> DocumentPipeline:
    """DocumentPipeline with schema initialized (dimensions given to skip the probe)."""
    pipe = DocumentPipeline(db=mock_client, embedding_dimensions=EMBEDDING_DIMENSIONS)
    await pipe.setup_schema()
    mock_client.query.reset_mock()
    return pipe
