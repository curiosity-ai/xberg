```python title="Python"
import asyncio
from xberg import (
    ExtractInput,
    extract,
    ExtractionConfig,
    ChunkingConfig,
    EmbeddingConfig,
    EmbeddingModelType,
)

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        chunking=ChunkingConfig(
            max_characters=500,
            overlap=50,
            embedding=EmbeddingConfig(
                model=EmbeddingModelType.preset("balanced"),
                normalize=True,
                batch_size=16
            )
        )
    )
    result = await extract(ExtractInput(uri="research_paper.pdf"), config)

    chunks_with_embeddings: list = []
    for chunk in result.results[0].chunks or []:
        if chunk.embedding:
            chunks_with_embeddings.append({
                "content": chunk.content[:100],
                "embedding_dims": len(chunk.embedding)
            })

    print(f"Chunks with embeddings: {len(chunks_with_embeddings)}")

asyncio.run(main())
```
