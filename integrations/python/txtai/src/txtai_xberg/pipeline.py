"""Xberg-backed document extraction pipeline."""

from __future__ import annotations

import asyncio
from typing import TYPE_CHECKING, Any, TypedDict

from xberg import ExtractInput, extract_batch

if TYPE_CHECKING:
    from xberg import (
        Chunk,
        ExtractedDocument,
        ExtractionConfig,
        ExtractionErrorItem,
        ExtractionResult,
    )


class ExtractionFailedError(RuntimeError):
    """Raised when Xberg reports one or more per-input extraction errors.

    The ``errors`` attribute holds the raw ``ExtractionErrorItem`` objects
    returned by Xberg, each carrying an ``index`` into the input list, the
    ``source`` that failed, a numeric ``code``, and a ``message``.
    """

    def __init__(self, errors: list[ExtractionErrorItem]) -> None:
        self.errors = errors
        detail = "; ".join(
            f"input[{error.index}] {error.source}: {error.message} (code {error.code})" for error in errors
        )
        super().__init__(f"Xberg extraction failed for {len(errors)} input(s): {detail}")


class DocumentMetadata(TypedDict):
    """Metadata extracted from a single document."""

    source: str
    mime_type: str
    title: str | None
    authors: list[str] | None
    languages: list[str] | None
    page_count: int


class ExtractionDocument(TypedDict):
    """A single document extraction result."""

    content: str
    metadata: DocumentMetadata


# A txtai ``Embeddings.index`` document: ``(id, text, tags)``. ``tags`` carries
# the segment's metadata as a mapping, which txtai stores and — with
# ``content=True`` — exposes as filterable columns. ~keep
IndexDocument = tuple[str, str, dict[str, Any]]


class XbergPipeline:
    """Xberg-backed document extraction pipeline.

    A plain callable class that accepts one or more document paths and
    returns structured extraction results suitable for any downstream
    pipeline framework — txtai workflows, LangChain loaders, or direct
    use with embeddings indices.

    Following txtai pipeline conventions, a single string input returns a
    single :class:`ExtractionDocument`, while a list input returns a list of
    them in input order. Extraction runs through Xberg's asynchronous
    ``extract_batch`` — a single native call that fans the inputs out across
    Xberg's internal worker pool (bounded by ``max_concurrent_extractions``),
    which is substantially faster than extracting each file on its own.

    To index into a ``txtai.Embeddings``, use :meth:`to_documents`. When the
    configured ``ExtractionConfig`` enables ``chunking``, it emits one
    ``(id, text, tags)`` document per Xberg chunk — segments sized for
    embedding — instead of one document per file.
    """

    def __init__(self, config: ExtractionConfig | None = None) -> None:
        """Initialize the pipeline.

        Args:
            config: A Xberg ``ExtractionConfig``. Pass one to control
                ``output_format``, ``force_ocr``, the nested ``ocr`` and
                ``chunking`` configuration, ``max_concurrent_extractions``, and
                every other Xberg knob. When omitted, Xberg's defaults apply.

                Example::

                    from xberg import ChunkingConfig, ExtractionConfig, OcrConfig

                    config = ExtractionConfig(
                        output_format="markdown",
                        ocr=OcrConfig(language="eng"),
                        force_ocr=True,
                        chunking=ChunkingConfig(max_characters=1000, overlap=200),
                        max_concurrent_extractions=8,
                    )
                    pipeline = XbergPipeline(config=config)

        """
        self._config = config

    def __call__(self, documents: str | list[str]) -> ExtractionDocument | list[ExtractionDocument]:
        """Extract text and metadata from one or more documents (synchronous).

        Bridges Xberg's async API to a synchronous call via :func:`asyncio.run`,
        so it must not be invoked from within a running event loop — use
        :meth:`acall` from async code instead.

        Args:
            documents: A single file path, or a list of file paths.

        Returns:
            A single :class:`ExtractionDocument` when ``documents`` is a string,
            or a list of them (in input order) when ``documents`` is a list.

        Raises:
            ExtractionFailedError: If Xberg reports any per-input errors.

        """
        return asyncio.run(self.acall(documents))

    async def acall(self, documents: str | list[str]) -> ExtractionDocument | list[ExtractionDocument]:
        """Extract text and metadata from one or more documents (asynchronous).

        The async counterpart to :meth:`__call__`, for callers already running
        inside an event loop.

        Args:
            documents: A single file path, or a list of file paths.

        Returns:
            A single :class:`ExtractionDocument` when ``documents`` is a string,
            or a list of them (in input order) when ``documents`` is a list.

        Raises:
            ExtractionFailedError: If Xberg reports any per-input errors.

        """
        if isinstance(documents, str):
            result = await self._extract([documents])
            return self._to_document(documents, result.results[0])
        paths = list(documents)
        result = await self._extract(paths)
        return [self._to_document(path, document) for path, document in zip(paths, result.results, strict=False)]

    def to_documents(self, documents: str | list[str]) -> list[IndexDocument]:
        """Extract and flatten into ``txtai.Embeddings.index``-ready documents.

        Synchronous wrapper around :meth:`ato_documents`.

        Args:
            documents: A single file path, or a list of file paths.

        Returns:
            A flat list of ``(id, text, tags)`` tuples ready to hand straight to
            ``Embeddings.index``. When the configured ``ExtractionConfig`` enables
            chunking, each Xberg chunk becomes one document; otherwise each file
            becomes a single document.

        Raises:
            ExtractionFailedError: If Xberg reports any per-input errors.

        """
        return asyncio.run(self.ato_documents(documents))

    async def ato_documents(self, documents: str | list[str]) -> list[IndexDocument]:
        """Extract and flatten into ``txtai.Embeddings.index``-ready documents.

        The async counterpart to :meth:`to_documents`. Prefer Xberg's native
        chunking (``ExtractionConfig(chunking=...)``) so segments arrive sized
        for the embedding model, with heading and page context preserved in each
        document's ``tags``.

        Args:
            documents: A single file path, or a list of file paths.

        Returns:
            A flat list of ``(id, text, tags)`` tuples across every input.

        Raises:
            ExtractionFailedError: If Xberg reports any per-input errors.

        """
        paths = [documents] if isinstance(documents, str) else list(documents)
        result = await self._extract(paths)
        index_documents: list[IndexDocument] = []
        for path, document in zip(paths, result.results, strict=False):
            index_documents.extend(self._to_index_documents(path, document))
        return index_documents

    async def _extract(self, paths: list[str]) -> ExtractionResult:
        inputs = [ExtractInput(uri=path) for path in paths]
        result: ExtractionResult = await extract_batch(inputs, self._config)
        if result.errors:
            # ``result.results`` only holds successful extractions, so zipping it
            # against ``paths`` would silently misalign. Fail loudly instead. ~keep
            raise ExtractionFailedError(list(result.errors))
        return result

    @staticmethod
    def _to_document(source: str, document: ExtractedDocument) -> ExtractionDocument:
        return ExtractionDocument(
            content=document.content,
            metadata=DocumentMetadata(
                source=source,
                mime_type=document.mime_type,
                title=document.metadata.title,
                authors=document.metadata.authors,
                languages=document.detected_languages,
                page_count=document.counts.pages,
            ),
        )

    @classmethod
    def _to_index_documents(cls, source: str, document: ExtractedDocument) -> list[IndexDocument]:
        chunks = document.chunks
        if not chunks:
            tags: dict[str, Any] = {
                "source": source,
                "mime_type": document.mime_type,
                "title": document.metadata.title,
                "page_count": document.counts.pages,
            }
            return [(source, document.content, tags)]
        return [cls._chunk_to_index_document(source, document, chunk) for chunk in chunks]

    @staticmethod
    def _chunk_to_index_document(source: str, document: ExtractedDocument, chunk: Chunk) -> IndexDocument:
        meta = chunk.metadata
        tags: dict[str, Any] = {
            "source": source,
            "mime_type": document.mime_type,
            "title": document.metadata.title,
            "chunk_index": meta.chunk_index,
            "total_chunks": meta.total_chunks,
            "heading_path": meta.heading_path,
            "first_page": meta.first_page,
            "last_page": meta.last_page,
            "token_count": meta.token_count,
        }
        return (f"{source}#{meta.chunk_index}", chunk.content, tags)
