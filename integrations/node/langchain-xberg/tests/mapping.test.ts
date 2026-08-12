import { describe, expect, it } from "vitest";
import type { Keyword, Metadata, Table } from "@xberg-io/xberg";
import {
  assembleContent,
  buildMetadata,
  documentToDocuments,
  flattenMetadata,
  isChunkingEnabled,
  isPerPageEnabled,
  resultToDocuments,
  type ChunkShape,
  type ExtractedDoc,
  type PageShape,
  type ResultEnvelope,
  type SplitOptions,
} from "../src/mapping";

const WHOLE_DOC: SplitOptions = { chunking: false, perPage: false };
const CHUNKING: SplitOptions = { chunking: true, perPage: false };
const PER_PAGE: SplitOptions = { chunking: false, perPage: true };

function makeTable(overrides: Partial<Table> = {}): Table {
  return {
    cells: [
      ["A", "B"],
      ["1", "2"],
    ],
    markdown: "| A | B |\n|---|---|\n| 1 | 2 |",
    pageNumber: 1,
    ...overrides,
  };
}

function makeKeyword(text: string, score: number, algorithm: string): Keyword {
  return { text, score, algorithm: algorithm as unknown as Keyword["algorithm"] };
}

function makePage(pageNumber: number, content: string, overrides: Partial<PageShape> = {}): PageShape {
  return { pageNumber, content, tables: [], ...overrides };
}

function makeChunk(content: string, metadata: Partial<ChunkShape["metadata"]>, chunkType = "unknown"): ChunkShape {
  return {
    content,
    chunkType,
    metadata: { chunkIndex: 0, totalChunks: 1, headingPath: [], ...metadata },
  };
}

function makeDoc(overrides: Partial<ExtractedDoc> = {}): ExtractedDoc {
  return {
    content: "Extracted text content",
    mimeType: "text/plain",
    metadata: {} as Metadata,
    tables: [],
    counts: { pages: 1, tables: 0, images: 0 },
    qualityScore: 1.0,
    ...overrides,
  };
}

function makeResult(results: ExtractedDoc[], errors?: ResultEnvelope["errors"]): ResultEnvelope {
  return { results, errors: errors ?? [] };
}

describe("isChunkingEnabled / isPerPageEnabled", () => {
  it("should_report_chunking_when_config_present", () => {
    expect(isChunkingEnabled({ chunking: { maxCharacters: 500 } } as never)).toBe(true);
    expect(isChunkingEnabled(undefined)).toBe(false);
    expect(isChunkingEnabled(null)).toBe(false);
  });

  it("should_report_per_page_when_extract_pages_truthy", () => {
    expect(isPerPageEnabled({ pages: { extractPages: true } } as never)).toBe(true);
    expect(isPerPageEnabled({ pages: { extractPages: false } } as never)).toBe(false);
    expect(isPerPageEnabled(undefined)).toBe(false);
  });
});

describe("flattenMetadata", () => {
  it("should_map_camelcase_to_snakecase_and_drop_nulls", () => {
    const metadata = {
      title: "Test Doc",
      authors: ["Alice", "Bob"],
      language: "en",
      createdAt: "2026-01-01",
      outputFormat: "markdown",
      ocrUsed: false,
    } as Metadata;

    const flat = flattenMetadata(metadata);

    expect(flat.title).toBe("Test Doc");
    expect(flat.authors).toEqual(["Alice", "Bob"]);
    expect(flat.language).toBe("en");
    expect(flat.created_at).toBe("2026-01-01");
    expect(flat.output_format).toBe("markdown");
    expect(flat.ocr_used).toBe(false);
    expect(flat).not.toHaveProperty("subject");
    expect(flat).not.toHaveProperty("keywords");
  });

  it("should_merge_additional_when_non_empty", () => {
    const flat = flattenMetadata({ additional: { customKey: "value" } } as Metadata);
    expect(flat.additional).toEqual({ customKey: "value" });
  });

  it("should_return_empty_for_missing_metadata", () => {
    expect(flattenMetadata(undefined)).toEqual({});
    expect(flattenMetadata({ additional: {} } as Metadata)).toEqual({});
  });
});

describe("assembleContent", () => {
  it("should_return_content_when_no_tables", () => {
    expect(assembleContent("Body", [])).toBe("Body");
    expect(assembleContent("Body", undefined)).toBe("Body");
    expect(assembleContent(undefined, [])).toBe("");
  });

  it("should_append_table_markdown_unconditionally", () => {
    const content = assembleContent("Text", [makeTable({ markdown: "| T1 |" }), makeTable({ markdown: "| T2 |" })]);
    expect(content).toBe("Text\n\n| T1 |\n\n| T2 |");
  });

  it("should_ignore_tables_without_markdown", () => {
    expect(assembleContent("Text", [makeTable({ markdown: "" })])).toBe("Text");
  });
});

describe("buildMetadata", () => {
  it("should_include_source_and_enrichment_fields", () => {
    const doc = makeDoc({
      mimeType: "application/pdf",
      metadata: { title: "Report", outputFormat: "markdown" } as Metadata,
      qualityScore: 0.85,
      detectedLanguages: ["eng", "deu"],
      counts: { pages: 3, tables: 0, images: 0 },
    });

    const metadata = buildMetadata(doc, "document.pdf");

    expect(metadata.source).toBe("document.pdf");
    expect(metadata.mime_type).toBe("application/pdf");
    expect(metadata.title).toBe("Report");
    expect(metadata.output_format).toBe("markdown");
    expect(metadata.quality_score).toBe(0.85);
    expect(metadata.detected_languages).toEqual(["eng", "deu"]);
    expect(metadata.page_count).toBe(3);
    expect(metadata.table_count).toBe(0);
  });

  it("should_serialize_extracted_keywords", () => {
    const doc = makeDoc({
      extractedKeywords: [makeKeyword("python", 0.95, "yake"), makeKeyword("machine learning", 0.88, "yake")],
    });

    const metadata = buildMetadata(doc, "doc.txt");
    const keywords = metadata.extracted_keywords as Array<Record<string, unknown>>;

    expect(keywords).toHaveLength(2);
    expect(keywords[0]).toEqual({ text: "python", score: 0.95, algorithm: "yake" });
    expect(keywords[1].text).toBe("machine learning");
  });

  it("should_serialize_tables_and_count", () => {
    const doc = makeDoc({
      tables: [
        makeTable({
          cells: [
            ["A", "B"],
            ["1", "2"],
          ],
          markdown: "| A | B |",
          pageNumber: 1,
        }),
      ],
    });

    const metadata = buildMetadata(doc, "doc.pdf");
    const tables = metadata.tables as Array<Record<string, unknown>>;

    expect(metadata.table_count).toBe(1);
    expect(tables).toHaveLength(1);
    expect(tables[0].cells).toEqual([
      ["A", "B"],
      ["1", "2"],
    ]);
    expect(tables[0].page_number).toBe(1);
  });

  it("should_serialize_processing_warnings", () => {
    const doc = makeDoc({
      processingWarnings: [
        { source: "extraction", message: "Low quality scan detected" },
        { source: "chunking", message: "Missing font fallback" },
      ],
    });

    const metadata = buildMetadata(doc, "doc.txt");
    const warnings = metadata.processing_warnings as Array<Record<string, unknown>>;

    expect(warnings).toHaveLength(2);
    expect(warnings[0]).toEqual({ source: "extraction", message: "Low quality scan detected" });
  });
});

describe("documentToDocuments split precedence", () => {
  it("should_prefer_chunks_over_pages_and_whole_doc", () => {
    const doc = makeDoc({
      content: "whole doc",
      chunks: [makeChunk("First chunk", { chunkIndex: 0, totalChunks: 2 })],
      pages: [makePage(1, "Page 1")],
    });

    const docs = documentToDocuments(doc, "doc.pdf", { chunking: true, perPage: true });

    expect(docs).toHaveLength(1);
    expect(docs[0].pageContent).toBe("First chunk");
  });

  it("should_prefer_pages_over_whole_doc_when_no_chunking", () => {
    const doc = makeDoc({
      content: "whole doc",
      pages: [makePage(1, "Page 1"), makePage(2, "Page 2")],
    });

    const docs = documentToDocuments(doc, "doc.pdf", PER_PAGE);

    expect(docs).toHaveLength(2);
    expect(docs[0].pageContent).toBe("Page 1");
    expect(docs[1].pageContent).toBe("Page 2");
  });

  it("should_fall_back_to_whole_doc_when_chunking_but_no_chunks", () => {
    const doc = makeDoc({ content: "Whole document", chunks: undefined });
    const docs = documentToDocuments(doc, "doc.txt", CHUNKING);
    expect(docs).toHaveLength(1);
    expect(docs[0].pageContent).toBe("Whole document");
  });

  it("should_fall_back_to_whole_doc_when_per_page_but_no_pages", () => {
    const doc = makeDoc({ content: "Whole document", pages: undefined });
    const docs = documentToDocuments(doc, "doc.txt", PER_PAGE);
    expect(docs).toHaveLength(1);
    expect(docs[0].pageContent).toBe("Whole document");
  });

  it("should_append_tables_to_whole_doc_content", () => {
    const doc = makeDoc({
      content: "Main text",
      tables: [makeTable({ markdown: "| Col1 | Col2 |\n|---|---|\n| A | B |" })],
    });
    const docs = documentToDocuments(doc, "doc.pdf", WHOLE_DOC);
    expect(docs[0].pageContent).toContain("Main text");
    expect(docs[0].pageContent).toContain("| Col1 | Col2 |");
  });
});

describe("per-page metadata", () => {
  it("should_rewrite_page_numbers_to_zero_indexed", () => {
    const doc = makeDoc({
      pages: [makePage(1, "Page 1", { isBlank: false }), makePage(2, "Page 2", { isBlank: true })],
      counts: { pages: 2, tables: 0, images: 0 },
    });

    const docs = documentToDocuments(doc, "doc.pdf", PER_PAGE);

    expect(docs[0].metadata.page).toBe(0);
    expect(docs[0].metadata.is_blank).toBe(false);
    expect(docs[1].metadata.page).toBe(1);
    expect(docs[1].metadata.is_blank).toBe(true);
    expect(docs[0].metadata.source).toBe("doc.pdf");
  });

  it("should_append_page_tables_to_page_content", () => {
    const doc = makeDoc({
      pages: [makePage(1, "Text", { tables: [makeTable({ markdown: "| X |\n|---|\n| Y |" })], isBlank: false })],
      counts: { pages: 1, tables: 0, images: 0 },
    });

    const docs = documentToDocuments(doc, "doc.pdf", PER_PAGE);
    expect(docs[0].pageContent).toContain("| X |");
  });
});

describe("per-chunk metadata", () => {
  it("should_carry_chunk_specific_keys_with_zero_indexed_page", () => {
    const doc = makeDoc({
      chunks: [
        makeChunk("Chunk text", {
          chunkIndex: 3,
          totalChunks: 10,
          headingPath: ["Chapter 1", "Section 2"],
          firstPage: 4,
          lastPage: 5,
          tokenCount: 42,
        }),
      ],
    });

    const docs = documentToDocuments(doc, "doc.pdf", CHUNKING);
    const metadata = docs[0].metadata;

    expect(metadata.chunk_index).toBe(3);
    expect(metadata.total_chunks).toBe(10);
    expect(metadata.heading_path).toEqual(["Chapter 1", "Section 2"]);
    expect(metadata.token_count).toBe(42);
    // 1-indexed first_page becomes 0-indexed "page". ~keep
    expect(metadata.page).toBe(3);
    expect(metadata.first_page).toBe(4);
    expect(metadata.last_page).toBe(5);
    expect(metadata.source).toBe("doc.pdf");
    expect(metadata.chunk_type).toBe("unknown");
  });

  it("should_omit_empty_heading_path_and_unset_page_and_token", () => {
    const doc = makeDoc({ chunks: [makeChunk("Text", { chunkIndex: 0, totalChunks: 1, headingPath: [] })] });
    const docs = documentToDocuments(doc, "doc.pdf", CHUNKING);
    const metadata = docs[0].metadata;

    expect(metadata).not.toHaveProperty("heading_path");
    expect(metadata).not.toHaveProperty("page");
    expect(metadata).not.toHaveProperty("token_count");
  });

  it("should_emit_one_document_per_chunk", () => {
    const doc = makeDoc({
      content: "whole doc",
      chunks: [
        makeChunk("First chunk", { chunkIndex: 0, totalChunks: 2, headingPath: ["Intro"], firstPage: 1 }),
        makeChunk("Second chunk", { chunkIndex: 1, totalChunks: 2, headingPath: ["Body"], firstPage: 2 }),
      ],
    });

    const docs = documentToDocuments(doc, "doc.pdf", CHUNKING);

    expect(docs).toHaveLength(2);
    expect(docs[0].pageContent).toBe("First chunk");
    expect(docs[0].metadata.page).toBe(0);
    expect(docs[1].pageContent).toBe("Second chunk");
    expect(docs[1].metadata.page).toBe(1);
  });
});

describe("resultToDocuments", () => {
  it("should_align_sources_positionally", () => {
    const result = makeResult([makeDoc(), makeDoc(), makeDoc()]);
    const docs = resultToDocuments(result, ["a.txt", "b.txt", "c.txt"], WHOLE_DOC);
    expect(docs.map((doc) => doc.metadata.source)).toEqual(["a.txt", "b.txt", "c.txt"]);
  });

  it("should_throw_on_first_error_fail_fast", () => {
    const result = makeResult(
      [makeDoc()],
      [
        { source: "bad.xyz", message: "unsupported format" },
        { source: "other.xyz", message: "second error" },
      ],
    );

    expect(() => resultToDocuments(result, ["good.txt", "bad.xyz"], WHOLE_DOC)).toThrowError(
      "Failed to extract 'bad.xyz': unsupported format",
    );
  });
});
