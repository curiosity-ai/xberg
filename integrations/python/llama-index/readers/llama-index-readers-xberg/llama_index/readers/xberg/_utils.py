"""Utility functions for XbergReader metadata and document construction.

xberg returns attribute-based objects (``ExtractedDocument``, ``Element``,
``ExtractedImage``, ``Metadata``, ``Table``). LlamaIndex ``Document.metadata``
must be JSON-serialisable, so the helpers here flatten those objects into plain
dicts and lists.
"""

import base64
import hashlib
from pathlib import Path
from typing import TYPE_CHECKING, Any

from llama_index.readers.xberg._types import (
    Annotation,
    DocumentMetadata,
    Keyword,
    ProcessingWarning,
    SerializedChunk,
    SerializedElement,
)

if TYPE_CHECKING:
    from xberg import (
        Chunk,
        Element,
        ExtractedDocument,
        ExtractedImage,
        Metadata,
        Table,
    )

# Scalar / list ``Metadata`` attributes copied verbatim into document metadata. ~keep
_METADATA_FIELDS = (
    "title",
    "subject",
    "authors",
    "keywords",
    "language",
    "created_at",
    "modified_at",
    "created_by",
    "modified_by",
    "category",
    "tags",
    "document_version",
    "abstract_text",
)


def _serialize_bounding_box(bbox: Any) -> dict[str, float] | None:
    """Flatten a ``BoundingBox`` object into a plain dict."""
    if bbox is None:
        return None
    return {"x0": bbox.x0, "y0": bbox.y0, "x1": bbox.x1, "y1": bbox.y1}


def serialize_metadata(metadata: "Metadata | None") -> dict[str, Any]:
    """Flatten an xberg ``Metadata`` object into a JSON-safe dict of set fields."""
    if metadata is None:
        return {}
    result: dict[str, Any] = {}
    for field in _METADATA_FIELDS:
        value = getattr(metadata, field, None)
        if value is not None:
            result[field] = value
    return result


def serialize_elements(elements: "list[Element]") -> list[SerializedElement]:
    """Serialize xberg ``Element`` objects into the reader/node-parser contract.

    Each element becomes ``{"text", "element_type", "metadata": {"page_number",
    "element_index"}}``. The ``element_type`` enum is stringified so the result
    is JSON-serialisable.
    """
    serialized: list[SerializedElement] = []
    for element in elements:
        element_meta = element.metadata
        serialized.append(
            {
                "text": element.text,
                "element_type": str(element.element_type),
                "metadata": {
                    "page_number": getattr(element_meta, "page_number", None),
                    "element_index": getattr(element_meta, "element_index", None),
                },
            }
        )
    return serialized


def serialize_chunks(chunks: "list[Chunk]") -> list[SerializedChunk]:
    """Serialize xberg native ``Chunk`` objects into the node-parser contract.

    Each chunk carries its markdown ``content``, a stringified ``chunk_type``,
    and the semantic metadata xberg's chunker produces (heading path, page
    span, chunk index). The companion ``XbergNodeParser`` turns each of these
    into a ``TextNode``.
    """
    serialized: list[SerializedChunk] = []
    for chunk in chunks:
        meta = chunk.metadata
        serialized.append(
            {
                "content": chunk.content,
                "chunk_type": str(chunk.chunk_type),
                "metadata": {
                    "chunk_index": meta.chunk_index,
                    "total_chunks": meta.total_chunks,
                    "first_page": meta.first_page,
                    "last_page": meta.last_page,
                    "heading_path": list(meta.heading_path),
                    "token_count": meta.token_count,
                },
            }
        )
    return serialized


def serialize_images(images: "list[ExtractedImage]", page_number: int | None = None) -> list[dict[str, Any]]:
    """Serialize image objects to JSON-safe dicts, filtering by page when given."""
    serialized: list[dict[str, Any]] = []
    for img in images:
        if page_number is not None and img.page_number != page_number:
            continue
        raw_data = img.data
        entry: dict[str, Any] = {
            "format": img.format,
            "image_index": img.image_index,
            "page_number": img.page_number,
            "width": img.width,
            "height": img.height,
            "colorspace": img.colorspace,
            "bits_per_component": img.bits_per_component,
            "is_mask": img.is_mask,
            "description": img.description,
            "data": base64.b64encode(raw_data).decode("ascii") if raw_data else None,
        }
        bbox = _serialize_bounding_box(img.bounding_box)
        if bbox is not None:
            entry["bounding_box"] = bbox
        ocr_result = img.ocr_result
        if ocr_result is not None:
            entry["ocr_result"] = ocr_result.content
        serialized.append(entry)
    return serialized


def build_metadata(
    document: "ExtractedDocument",
    file_path: Path | None = None,
    source: str | None = None,
    extra_info: dict[str, Any] | None = None,
    page_number: int | None = None,
) -> DocumentMetadata:
    """Flatten an ``ExtractedDocument`` into a JSON-serialisable metadata dict."""
    meta: DocumentMetadata = {}

    if file_path is not None:
        meta["file_name"] = file_path.name
        meta["file_path"] = str(file_path)
    elif source is not None:
        meta["file_name"] = source
        meta["file_path"] = source

    meta["file_type"] = document.mime_type
    meta["total_pages"] = document.counts.pages

    if page_number is not None:
        meta["page_number"] = page_number

    meta.update(serialize_metadata(document.metadata))
    meta["output_format"] = document.metadata.output_format

    if document.quality_score is not None:
        meta["quality_score"] = document.quality_score
    if document.detected_languages is not None:
        meta["detected_languages"] = document.detected_languages
    if document.processing_warnings:
        meta["processing_warnings"] = [
            ProcessingWarning(source=w.source, message=w.message) for w in document.processing_warnings
        ]
    if document.extracted_keywords:
        meta["extracted_keywords"] = [
            Keyword(text=kw.text, score=kw.score, algorithm=str(kw.algorithm)) for kw in document.extracted_keywords
        ]
    if document.annotations:
        meta["annotations"] = [
            Annotation(
                annotation_type=str(a.annotation_type),
                content=a.content,
                page_number=a.page_number,
            )
            for a in document.annotations
        ]
    if document.elements is not None:
        meta["_xberg_elements"] = serialize_elements(document.elements)
    if document.chunks:
        meta["_xberg_chunks"] = serialize_chunks(document.chunks)
    if document.images:
        meta["images"] = serialize_images(document.images, page_number=page_number)

    if extra_info:
        meta.update(extra_info)

    return meta


def generate_doc_id(
    *,
    file_path: Path | None = None,
    data: bytes | None = None,
    page_number: int | None = None,
) -> str:
    """Generate a deterministic document ID via SHA-256."""
    if file_path is None and data is None:
        msg = "Either file_path or data must be provided"
        raise ValueError(msg)
    hasher = hashlib.sha256()
    if file_path is not None:
        hasher.update(str(file_path.resolve()).encode())
    elif data is not None:
        hasher.update(data)
    if page_number is not None:
        hasher.update(str(page_number).encode())
    return hasher.hexdigest()


def excluded_keys(meta: DocumentMetadata) -> list[str]:
    """Return metadata keys that should be excluded from LLM/embedding input."""
    keys: list[str] = []
    if "_xberg_elements" in meta:
        keys.append("_xberg_elements")
    if "_xberg_chunks" in meta:
        keys.append("_xberg_chunks")
    if "images" in meta:
        keys.append("images")
    return keys


def append_tables(content: str, tables: "list[Table]") -> str:
    """Append table markdown to content when tables are not already included."""
    if not tables:
        return content
    for table in tables:
        table_md = table.markdown
        if table_md and table_md.strip() not in content:
            content = content.rstrip() + "\n\n" + table_md
    return content
