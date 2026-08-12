```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, TranslationConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        translation=TranslationConfig(
            target_lang="de",
            preserve_markup=True,
            llm=LlmConfig(model="openai/gpt-4o-mini"),
        ),
    )
    result = await extract(ExtractInput(uri="contract.pdf"), config)
    if result.results[0].translation:
        print(result.results[0].translation.content)

asyncio.run(main())
```
