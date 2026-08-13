```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, SummarizationConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        summarization=SummarizationConfig(
            strategy="abstractive",
            max_tokens=300,
            llm=LlmConfig(model="openai/gpt-4o-mini"),
        ),
    )
    result = await extract(ExtractInput(uri="report.pdf"), config)
    if result.results[0].summary:
        print(result.results[0].summary.text)

asyncio.run(main())
```
