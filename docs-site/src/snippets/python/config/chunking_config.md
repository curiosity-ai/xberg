```python title="Python"
import asyncio
from xberg import ExtractInput, ExtractionConfig, ChunkingConfig, extract

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        chunking=ChunkingConfig(
            max_characters=1000,
            overlap=200,
        )
    )
    result = await extract(ExtractInput(uri="document.pdf"), config)
    chunks = result.results[0].chunks or []
    print(f"Chunks: {len(chunks)}")
    for chunk in chunks:
        print(f"Length: {len(chunk.content)}")

asyncio.run(main())
```

```python title="Python - Markdown with Heading Context"
import asyncio
from xberg import ExtractInput, ExtractionConfig, ChunkingConfig, extract, ChunkSizing

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        chunking=ChunkingConfig(
            chunker_type="markdown",
            max_characters=500,
            overlap=50,
            sizing=ChunkSizing.tokenizer(model="Xenova/gpt-4o"),
        )
    )
    result = await extract(ExtractInput(uri="document.md"), config)
    for chunk in result.results[0].chunks or []:
        heading_context = chunk.metadata.heading_context
        if heading_context:
            for h in heading_context.headings:
                print(f"Heading L{h.level}: {h.text}")
        print(f"Content: {chunk.content[:100]}...")

asyncio.run(main())
```

```python title="Python - Semantic"
import asyncio
from xberg import ExtractInput, ExtractionConfig, ChunkingConfig, extract

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        chunking=ChunkingConfig(chunker_type="semantic")
    )
    result = await extract(ExtractInput(uri="document.pdf"), config)
    for chunk in result.results[0].chunks or []:
        print(f"Content: {chunk.content[:100]}...")

asyncio.run(main())
```

```python title="Python - Prepend Heading Context"
import asyncio
from xberg import ExtractInput, ExtractionConfig, ChunkingConfig, extract

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        chunking=ChunkingConfig(
            chunker_type="markdown",
            max_characters=500,
            overlap=50,
            prepend_heading_context=True,
        )
    )
    result = await extract(ExtractInput(uri="document.md"), config)
    for chunk in result.results[0].chunks or []:
        # Each chunk's content is prefixed with its heading breadcrumb
        print(f"Content: {chunk.content[:100]}...")

asyncio.run(main())
```
