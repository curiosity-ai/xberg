```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, TokenReductionOptions

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        token_reduction=TokenReductionOptions(
            mode="moderate", preserve_important_words=True
        )
    )
    result = await extract(ExtractInput(uri="verbose_document.pdf"), config)
    print(f"Reduced content length: {len(result.results[0].content)} chars")

asyncio.run(main())
```
