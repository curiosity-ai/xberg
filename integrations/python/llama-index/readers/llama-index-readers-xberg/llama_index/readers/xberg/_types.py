"""Internal type definitions for XbergReader metadata.

These describe the JSON-serialisable dict shapes that ``_utils`` produces from
xberg's attribute-based objects (``ExtractedDocument``, ``Element``,
``ExtractedImage``, ``Metadata``). LlamaIndex ``Document.metadata`` must be
JSON-serialisable, so every xberg object is flattened into plain dicts here.
"""

from typing import Any, TypedDict


class ProcessingWarning(TypedDict):
    source: str
    message: str


class Keyword(TypedDict):
    text: str
    score: float
    algorithm: str


class Annotation(TypedDict):
    annotation_type: str
    content: str | None
    page_number: int


class SerializedElementMetadata(TypedDict):
    page_number: int | None
    element_index: int | None


class SerializedElement(TypedDict):
    """JSON-serialisable element dict stored under ``_xberg_elements``.

    The companion ``XbergNodeParser`` reads these back with dict access
    (``el.get("text")``, ``el["metadata"].get("page_number")``), so this shape
    is the contract between the reader and the node parser.
    """

    text: str
    element_type: str
    metadata: SerializedElementMetadata


class SerializedChunkMetadata(TypedDict):
    chunk_index: int
    total_chunks: int
    first_page: int | None
    last_page: int | None
    heading_path: list[str]
    token_count: int | None


class SerializedChunk(TypedDict):
    """JSON-serialisable chunk dict stored under ``_xberg_chunks``.

    Produced when the caller enables ``ExtractionConfig(chunking=...)``. xberg's
    native chunker splits on semantic boundaries and carries heading and page
    context, so these are the preferred input for ``XbergNodeParser`` — richer
    than raw elements. The shape is the contract between reader and node parser.
    """

    content: str
    chunk_type: str
    metadata: SerializedChunkMetadata


# Document metadata is an open-ended mapping: it carries the flattened xberg
# Metadata scalars plus reader-specific keys and arbitrary user ``extra_info``. ~keep
DocumentMetadata = dict[str, Any]
