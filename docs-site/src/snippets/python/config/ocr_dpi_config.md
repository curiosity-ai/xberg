```python title="Python"
import asyncio
from xberg import (
    ExtractInput,
    extract,
    ExtractionConfig,
    OcrConfig,
    TesseractConfig,
    ImagePreprocessingConfig,
)

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        ocr=OcrConfig(
            backend="tesseract",
            tesseract_config=TesseractConfig(
                preprocessing=ImagePreprocessingConfig(target_dpi=300),
            ),
        ),
    )

    result = await extract(ExtractInput(uri="scanned.pdf"), config)

    content_length: int = len(result.results[0].content)
    table_count: int = len(result.results[0].tables)

    print(f"Content length: {content_length} characters")
    print(f"Tables detected: {table_count}")

asyncio.run(main())
```
