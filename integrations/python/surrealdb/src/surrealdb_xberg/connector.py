"""Full-document extraction and BM25 search connector."""

from xberg import ExtractedDocument

from surrealdb_xberg._base import BaseIngester, _execute_insert, _map_result_to_doc
from surrealdb_xberg.schema import build_connector_schema


class DocumentConnector(BaseIngester):
    """Full-document extraction and BM25 search. No chunking or embedding."""

    ANALYZER_NAME: str = "doc_analyzer"

    @property
    def analyzer_name(self) -> str:
        """The BM25 analyzer name used in the schema."""
        return self.ANALYZER_NAME

    async def setup_schema(
        self,
        *,
        analyzer_language: str = "english",
        bm25_k1: float = 1.2,
        bm25_b: float = 0.75,
    ) -> None:
        """Create the documents table with BM25 index.

        Args:
            analyzer_language: Snowball stemmer language for the BM25 analyzer.
            bm25_k1: BM25 term-frequency saturation parameter.
            bm25_b: BM25 document-length normalization parameter.

        """
        stmts = build_connector_schema(
            table=self._table,
            analyzer_language=analyzer_language,
            bm25_k1=bm25_k1,
            bm25_b=bm25_b,
        )
        for stmt in stmts:
            await self._client.query(stmt)
        self._schema_ready = True

    async def _ingest_batch(self, documents: list[tuple[ExtractedDocument, str]]) -> None:
        """Map every document to a row and store them in one ``INSERT IGNORE``.

        Args:
            documents: ``(document, source)`` pairs to persist.

        """
        if not documents:
            return
        rows = [_map_result_to_doc(document, source, self._table) for document, source in documents]
        await _execute_insert(
            self._client,
            f"INSERT IGNORE INTO {self._table} $records",
            list(rows),
            context="document insertion",
        )
