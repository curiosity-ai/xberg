```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, OcrConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        force_ocr=True,
        ocr=OcrConfig(
            backend="vlm",
            vlm_config=LlmConfig(model="openai/gpt-4o-mini"),
        ),
    )
    result = await extract(ExtractInput(uri="scan.pdf"), config)
    print(result.results[0].content)

asyncio.run(main())
```
