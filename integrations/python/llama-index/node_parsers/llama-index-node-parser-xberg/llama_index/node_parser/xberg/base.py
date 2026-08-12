"""Structure-aware node parser for xberg-extracted documents."""

import logging
import sys
from collections.abc import Sequence
from typing import Any

if sys.version_info >= (3, 12):
    from typing import override
else:
    from typing_extensions import override

from llama_index.core.node_parser import NodeParser
from llama_index.core.schema import BaseNode, Document, NodeRelationship, TextNode
from llama_index.core.utils import get_tqdm_iterable

logger = logging.getLogger(__name__)

_ELEMENT_METADATA_KEYS = ("element_type", "page_number", "element_index")
_CHUNK_METADATA_KEYS = (
    "chunk_type",
    "heading_path",
    "page_number",
    "first_page",
    "last_page",
    "chunk_index",
    "total_chunks",
    "token_count",
)

_FORWARDED_KEYS = ("_xberg_chunks", "_xberg_elements")

_MISSING_ELEMENTS_WARNING = (
    "Document %s has no '_xberg_chunks' or '_xberg_elements' metadata. "
    "Passing through unchanged. Use XbergReader with "
    "ExtractionConfig(chunking=ChunkingConfig(...)) for native chunk nodes, or "
    "ExtractionConfig(result_format='element_based') for element nodes."
)


class XbergNodeParser(NodeParser):
    """Structure-aware node parser for xberg-extracted documents.

    Turns xberg's output into individual ``TextNode`` objects, preserving
    document structure through the RAG pipeline. It prefers xberg's native
    **chunks** (``_xberg_chunks``) — semantic splits carrying heading path and
    page span — and falls back to structural **elements** (``_xberg_elements``)
    when chunks are absent.

    Produce chunk-bearing documents with ``XbergReader`` configured with
    ``ExtractionConfig(chunking=ChunkingConfig(...))``; element-bearing
    documents with ``result_format="element_based"``. Documents carrying
    neither pass through unchanged with a warning.
    """

    @classmethod
    def class_name(cls) -> str:
        """Return the unique class identifier for serialisation."""
        return "XbergNodeParser"

    @override
    def _parse_nodes(
        self,
        nodes: Sequence[BaseNode],
        show_progress: bool = False,
        **kwargs: Any,
    ) -> list[BaseNode]:
        output: list[BaseNode] = []
        nodes_with_progress = get_tqdm_iterable(nodes, show_progress, "Parsing nodes")

        for node in nodes_with_progress:
            chunks = node.metadata.get("_xberg_chunks")
            if isinstance(chunks, list) and chunks:
                output.extend(self._nodes_from_chunks(node, chunks))
                continue

            elements = node.metadata.get("_xberg_elements")
            if isinstance(elements, list) and elements:
                output.extend(self._nodes_from_elements(node, elements))
                continue

            logger.warning(_MISSING_ELEMENTS_WARNING, node.node_id)
            output.append(node)

        return output

    def _new_text_node(self, text: str, index: int, source: BaseNode, metadata: dict[str, Any]) -> TextNode:
        """Build a child ``TextNode`` inheriting formatting from its source."""
        return TextNode(
            text=text,
            id_=self.id_func(index, source),
            metadata=metadata,
            excluded_llm_metadata_keys=list(source.excluded_llm_metadata_keys),
            metadata_separator=source.metadata_separator,
            metadata_template=source.metadata_template,
            text_template=source.text_template,
            relationships={NodeRelationship.SOURCE: source.as_related_node_info()},
        )

    def _nodes_from_chunks(self, node: BaseNode, chunks: list[dict[str, Any]]) -> list[TextNode]:
        """Split a chunk-bearing Document into one TextNode per non-empty chunk."""
        excluded_embed = list(node.excluded_embed_metadata_keys) + list(_CHUNK_METADATA_KEYS)
        result: list[TextNode] = []
        idx = 0
        for chunk in chunks:
            text = chunk.get("content", "")
            if not text.strip():
                continue
            meta = chunk.get("metadata", {})
            text_node = self._new_text_node(
                text,
                idx,
                node,
                {
                    "chunk_type": chunk.get("chunk_type", "unknown"),
                    "heading_path": meta.get("heading_path", []),
                    "page_number": meta.get("first_page"),
                    "first_page": meta.get("first_page"),
                    "last_page": meta.get("last_page"),
                    "chunk_index": meta.get("chunk_index"),
                    "total_chunks": meta.get("total_chunks"),
                    "token_count": meta.get("token_count"),
                },
            )
            text_node.excluded_embed_metadata_keys = excluded_embed
            result.append(text_node)
            idx += 1
        return result

    def _nodes_from_elements(self, node: BaseNode, elements: list[dict[str, Any]]) -> list[TextNode]:
        """Split an element-bearing Document into one TextNode per non-empty element."""
        excluded_embed = list(node.excluded_embed_metadata_keys) + list(_ELEMENT_METADATA_KEYS)
        result: list[TextNode] = []
        idx = 0
        for el in elements:
            text = el.get("text", "")
            if not text.strip():
                continue
            el_meta = el.get("metadata", {})
            text_node = self._new_text_node(
                text,
                idx,
                node,
                {
                    "element_type": el.get("element_type", "unknown"),
                    "page_number": el_meta.get("page_number"),
                    "element_index": el_meta.get("element_index"),
                },
            )
            text_node.excluded_embed_metadata_keys = excluded_embed
            result.append(text_node)
            idx += 1
        return result

    @staticmethod
    def _strip_forwarded_metadata(nodes: list[BaseNode]) -> list[BaseNode]:
        """Remove reader forwarding keys from child TextNodes only.

        Passthrough documents keep their metadata untouched.
        """
        for node in nodes:
            if node.source_node is not None:
                for key in _FORWARDED_KEYS:
                    node.metadata.pop(key, None)
        return nodes

    @override
    def _postprocess_parsed_nodes(self, nodes: list[BaseNode], parent_doc_map: dict[str, Document]) -> list[BaseNode]:
        nodes = super()._postprocess_parsed_nodes(nodes, parent_doc_map)
        return self._strip_forwarded_metadata(nodes)
