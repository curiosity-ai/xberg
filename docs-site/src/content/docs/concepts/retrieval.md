---
title: "Retrieval Modes"
---

Xberg supports four retrieval primitives — dense embeddings, sparse (SPLADE) embeddings, ColBERT late-interaction, and cross-encoder reranking — plus a hybrid mode that fuses the first three with reciprocal rank fusion (RRF). Each primitive is a separate model family with its own trade-offs; pick based on latency budget, index size, and accuracy requirements.

## Dense embeddings

A single-vector embedding per text, compared by cosine similarity or dot product. This is the fastest retrieval primitive — a vector index (HNSW, IVF, brute-force KNN) scales to millions of documents with sub-millisecond lookups. The cost is that queries and documents are encoded independently, so the model never sees them together.

Configure via `EmbeddingConfig` (`EmbeddingModelType::Preset { name }`):

| Preset | Dimensions | Pooling | Context | Notes |
| --- | --- | --- | --- | --- |
| `gte-modernbert-base` | 768 | cls | 8192 | Default. General-purpose English RAG with long-context ModernBERT tokenization. |
| `fast` | 384 | mean | — | all-MiniLM-L6-v2 quantized. Prototyping, resource-constrained environments. |
| `balanced` | 768 | cls | — | BGE-base-en-v1.5. General-purpose production RAG. |
| `quality` | 1024 | cls | — | BGE-large-en-v1.5. Maximum accuracy, higher compute. |
| `multilingual` | 768 | mean | — | multilingual-e5-base. 100+ languages. |
| `lightweight` | 256 | mean | — | potion-base-8M (model2vec). Pure-Rust static backend — no ONNX Runtime. Runs on WASM, Android, and other no-ORT targets. |
| `arctic-embed-m-v2.0` | 768 | cls | — | Snowflake Arctic-Embed-M v2.0, multilingual. Asymmetric: queries are auto-prefixed `"query: "`; document text is embedded verbatim. |
| `qwen3-embedding-0.6b` | 1024 | last-token | 32k | Decoder-style model. Highest-quality multilingual/long-context retrieval when compute allows. |

Use `lightweight` when ONNX Runtime is unavailable or undesirable (WASM bundles, Android x86_64 emulator, minimal-dependency deployments). Use `arctic-embed-m-v2.0` or `qwen3-embedding-0.6b` when query/document roles are known and long-context or multilingual coverage matters more than raw speed. See [Embeddings](/guides/embeddings/).

## Sparse embeddings (SPLADE)

A high-dimensional, mostly-zero vocabulary-space vector per text, stored as parallel `(indices, values)` arrays. SPLADE learns term expansion and weighting the way a neural model would, while remaining compatible with inverted-index-style sparse retrieval (BM25-like scoring, but learned rather than heuristic). Sparse retrieval complements dense embeddings: it captures exact-term and rare-term matches that a dense encoder can blur.

Configure via `SparseEmbeddingConfig` (`SparseEmbeddingModelType::Preset { name }`):

- `opensearch-v3-distill` — default. OpenSearch's distilled SPLADE model.
- `Splade_PP_en_v1` — fallback, English-only.

Sparse embeddings pair naturally with dense embeddings in the hybrid arm below — use sparse alone when you need exact keyword recall (product SKUs, error codes, identifiers) without full-text infrastructure.

## ColBERT late-interaction

A *sequence* of per-token vectors per text (one row per input token, including the ColBERT `[Q]`/`[D]` marker), instead of a single pooled vector. Retrieval scores a query against a document with MaxSim: for each query token, take the maximum dot product over all document token rows, then sum across query rows. This preserves token-level interaction — closer to a cross-encoder's accuracy — while still allowing precomputed document vectors and index-time scoring.

Configure via `LateInteractionConfig` (`LateInteractionModelType::Preset { name }`):

- `gte-moderncolbert` — default, 128-dim per-token vectors.
- `colbert-small-v1` — fallback, 96-dim.

Late-interaction costs more storage than dense (one vector per token, not per document) and more compute per query (MaxSim over all token pairs), but closes much of the accuracy gap to a full cross-encoder without the reranking pass's per-candidate latency.

## Reranking (cross-encoders)

Dense, sparse, and late-interaction retrieval are first-pass — they narrow millions of documents to a candidate set. Reranking is the second pass: a cross-encoder scores each `(query, document)` pair jointly, attending across both in every transformer layer, for the most accurate relevance judgment at the cost of one forward pass per candidate.

Configure via `RerankerConfig` (`RerankerModelType::Preset { name }`):

- `ettin-reranker-150m` — default. ModernBERT-based cross-encoder, long-context (up to ~8000 tokens), English.
- `qwen3-reranker-0.6b` — generative alternative. A causal LM repurposed as a reranker: relevance is read off the last token's "yes"/"no" logits, softmaxed into a probability. Higher quality, higher latency than a classic cross-encoder head.

See [Reranking](/guides/reranking/) for the full preset catalog, custom HuggingFace models, and the in-process plugin backend.

## Hybrid retrieval (reciprocal rank fusion)

Xberg has no built-in hybrid retrieval mode. It gives you the arms — dense, sparse, and late-interaction embeddings plus a reranker — and you combine them in your own retrieval layer, alongside whatever full-text index you already run. This section describes the fusion technique; [Retrieval](/guides/retrieval/) shows it implemented by hand.

The usual approach runs up to three arms in parallel — dense vector KNN, full-text/BM25-style search, and sparse SPLADE — then fuses their rankings with Reciprocal Rank Fusion (RRF):

```text
rrf_score(doc) = sum over arms( 1 / (k + rank_in_arm + 1) )
```

Each arm contributes `1 / (k + rank + 1)` to a document's fused score, where `rank` is that document's 0-indexed position within the arm's own result list. Documents missing from an arm simply don't contribute for that arm — RRF needs no arm-specific score normalization, which is what makes it a robust way to combine dense cosine similarity, BM25-style text scores, and sparse dot products, three otherwise incomparable scales. Results are sorted by fused RRF score descending.

Only the arms you actually run contribute, so a fusion over a full-text index plus `embed_texts` results is as valid as one that also folds in `embed_sparse`. Late-interaction is best kept out of the fusion: `max_sim_score` is a reranking-strength signal over a small candidate set, not a first-pass arm, so run it — or `rerank` — *after* the fusion rather than inside it.

## Choosing a mode

| Need | Use |
| --- | --- |
| Fast first-pass retrieval over a large corpus | Dense embeddings |
| Exact-term / rare-term recall (IDs, codes, keywords) | Sparse embeddings |
| Best first-pass accuracy at the cost of storage/compute | ColBERT late-interaction |
| Sharpen a small candidate set before it reaches an LLM | Reranking |
| Combine dense, full-text, and sparse strengths in one query | Fuse the arms yourself with RRF |
