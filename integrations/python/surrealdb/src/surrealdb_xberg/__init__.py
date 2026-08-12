"""Xberg-to-SurrealDB connector for zero-dependency RAG pipelines."""

from surrealdb_xberg._base import AsyncSurrealQueryable
from surrealdb_xberg.connector import DocumentConnector
from surrealdb_xberg.exceptions import DimensionMismatchError, IngestionError, SchemaNotInitializedError
from surrealdb_xberg.pipeline import DocumentPipeline
from surrealdb_xberg.types import ChunkRecord, DocumentRecord

__all__ = [
    "AsyncSurrealQueryable",
    "ChunkRecord",
    "DimensionMismatchError",
    "DocumentConnector",
    "DocumentPipeline",
    "DocumentRecord",
    "IngestionError",
    "SchemaNotInitializedError",
]
