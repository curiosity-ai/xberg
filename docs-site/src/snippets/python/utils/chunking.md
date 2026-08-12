```python title="Python"
from xberg import (
    ExtractionConfig,
    ChunkingConfig,
    EmbeddingConfig,
    EmbeddingModelType,
)

config: ExtractionConfig = ExtractionConfig(
    chunking=ChunkingConfig(
        max_characters=1500,
        overlap=200,
        embedding=EmbeddingConfig(
            model=EmbeddingModelType.preset("balanced")
        ),
    )
)
```
