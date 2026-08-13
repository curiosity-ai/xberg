```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, CaptioningConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        captioning=CaptioningConfig(
            llm=LlmConfig(model="openai/gpt-4o-mini"),
            min_image_area=0,
        ),
    )
    result = await extract(ExtractInput(uri="report.pdf"), config)
    for image in result.results[0].images or []:
        if image.caption:
            print(image.caption)

asyncio.run(main())
```
