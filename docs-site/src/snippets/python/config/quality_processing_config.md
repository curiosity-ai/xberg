```python title="Python"
import asyncio
from xberg import ExtractInput, ExtractionConfig, extract

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        enable_quality_processing=True
    )
    result = await extract(ExtractInput(uri="document.pdf"), config)

    quality_score: float = result.results[0].quality_score or 0.0
    print(f"Quality score: {quality_score:.2f}")

asyncio.run(main())
```
