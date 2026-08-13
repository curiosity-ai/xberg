```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, NerConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        ner=NerConfig(
            backend="llm",
            llm=LlmConfig(model="openai/gpt-4o-mini"),
        ),
    )
    result = await extract(ExtractInput(uri="contract.pdf"), config)
    for entity in result.results[0].entities or []:
        print(f"{entity.category}: {entity.text} (confidence={entity.confidence})")

asyncio.run(main())
```
