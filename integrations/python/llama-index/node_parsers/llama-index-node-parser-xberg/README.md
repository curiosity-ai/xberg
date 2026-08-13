# LlamaIndex Node Parser Xberg

<div align="center" style="display: flex; flex-wrap: wrap; gap: 8px; justify-content: center; margin: 20px 0;">
  <a href="https://pypi.org/project/llama-index-node-parser-xberg/">
    <img src="https://img.shields.io/pypi/v/llama-index-node-parser-xberg?label=Node%20Parser&color=007ec6" alt="Node Parser">
  </a>
  <a href="https://pypi.org/project/xberg/">
    <img src="https://img.shields.io/pypi/v/xberg?label=Xberg&color=007ec6" alt="Xberg">
  </a>
  <a href="https://github.com/xberg-io/llama-index-xberg/blob/main/LICENSE">
    <img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License">
  </a>
  <a href="https://docs.xberg.io">
    <img src="https://img.shields.io/badge/docs-xberg.io-blue" alt="Documentation">
  </a>
</div>

<img width="3384" height="573" alt="Xberg Banner" src="https://github.com/user-attachments/assets/1b6c6ad7-3b6d-4171-b1c9-f2026cc9deb8" />

<div align="center" style="margin-top: 20px;">
  <a href="https://discord.gg/xt9WY3GnKR">
    <img height="22" src="https://img.shields.io/badge/Discord-Join%20our%20community-7289da?logo=discord&logoColor=white" alt="Discord">
  </a>
</div>

Structure-aware LlamaIndex node parser for xberg-extracted documents. It turns
xberg's native **chunks** into nodes, and falls back to structural **elements**
when chunks are absent.

## Installation

```bash
pip install llama-index-node-parser-xberg
```

Requires `llama-index-core>=0.14.23,<0.15`. This package does not depend on
`xberg` directly — `xberg` is a dependency of the reader
(`llama-index-readers-xberg`), which produces the documents this parser splits.

## Prerequisites

> **This parser requires documents with `_xberg_chunks` or `_xberg_elements`
> metadata.** These are produced by `XbergReader`. Prefer native chunking; use
> element-based extraction when you want one node per structural element.
> Documents carrying neither pass through unchanged with a warning.

```python
from xberg import ChunkingConfig, ExtractionConfig
from llama_index.readers.xberg import XbergReader

# Preferred: native semantic chunks with heading path and page span.
reader = XbergReader(
    extraction_config=ExtractionConfig(chunking=ChunkingConfig(max_characters=1000, overlap=200))
)
documents = reader.load_data("report.pdf")
```

## Features

- Chunk-aware splitting — each xberg native chunk becomes a node, carrying `chunk_type`, `heading_path`, and page span
- Element fallback — when no chunks are present, headings, paragraphs, tables, and code blocks each become a node
- Source and prev/next relationships tracked via `NodeRelationship`
- Graceful degradation — documents without chunk or element metadata pass through with a warning
- Composes with other transformations (e.g., `SentenceSplitter`)
- Async support via `aget_nodes_from_documents`
- Serialization support (`to_dict` / `from_dict`)

## Usage

### Basic

Full reader-to-nodes flow:

```python
from xberg import ChunkingConfig, ExtractionConfig
from llama_index.readers.xberg import XbergReader
from llama_index.node_parser.xberg import XbergNodeParser

reader = XbergReader(
    extraction_config=ExtractionConfig(chunking=ChunkingConfig(max_characters=1000, overlap=200))
)
documents = reader.load_data("report.pdf")

parser = XbergNodeParser()
nodes = parser.get_nodes_from_documents(documents)
```

### IngestionPipeline

Chain with `SentenceSplitter` to further split any oversized nodes:

```python
from llama_index.core.ingestion import IngestionPipeline
from llama_index.core.node_parser import SentenceSplitter

pipeline = IngestionPipeline(
    transformations=[
        XbergNodeParser(),
        SentenceSplitter(chunk_size=512),  # Further split large nodes
    ]
)
nodes = pipeline.run(documents=documents)
```

### VectorStoreIndex

Using the `transformations` parameter:

```python
from llama_index.core import VectorStoreIndex

index = VectorStoreIndex.from_documents(
    documents,
    transformations=[XbergNodeParser()],
)
```

### Async

```python
nodes = await parser.aget_nodes_from_documents(documents)
```

## Behavior Notes

- Chunks take priority over elements. When a document carries both
  `_xberg_chunks` and `_xberg_elements`, the parser splits on chunks.
- Documents without either metadata key pass through unchanged with a warning.
  This is intentional — silently falling back would hide that you are not
  getting structure-aware splitting.
- Empty or whitespace-only chunks and elements are automatically skipped.
