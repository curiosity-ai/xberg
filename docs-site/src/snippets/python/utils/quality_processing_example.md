```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig

async def main() -> None:
    config = ExtractionConfig(enable_quality_processing=True)
    result = await extract(ExtractInput(uri="scanned_document.pdf"), config)

    quality_score = result.results[0].quality_score or 0.0

    if quality_score < 0.5:
        print(f"Warning: Low quality extraction ({quality_score:.2f})")
        print("Consider re-scanning with higher DPI or adjusting OCR settings")
    else:
        print(f"Quality score: {quality_score:.2f}")

asyncio.run(main())
```
