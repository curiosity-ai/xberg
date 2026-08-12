```python title="Python"
import asyncio
from xberg import ExtractInput, ExtractionConfig, LanguageDetectionConfig, extract

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        language_detection=LanguageDetectionConfig(
            enabled=True,
            min_confidence=0.85,
            detect_multiple=False
        )
    )
    result = await extract(ExtractInput(uri="document.pdf"), config)
    if result.results[0].detected_languages:
        print(f"Primary language: {result.results[0].detected_languages[0]}")
    print(f"Content length: {len(result.results[0].content)} chars")

asyncio.run(main())
```
