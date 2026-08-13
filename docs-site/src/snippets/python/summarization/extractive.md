```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, SummarizationConfig

async def main() -> None:
    config = ExtractionConfig(
        summarization=SummarizationConfig(
            strategy="extractive",
            max_tokens=200,
        ),
    )
    result = await extract(ExtractInput(uri="report.pdf"), config)
    if result.results[0].summary:
        print(result.results[0].summary.text)

asyncio.run(main())
```
