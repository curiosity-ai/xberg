```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig
from xberg import (
    XbergError,
    ParsingError,
    OcrError,
    ValidationError,
)

async def main() -> None:
    try:
        result = await extract(ExtractInput(uri="document.pdf"), ExtractionConfig())
        print(f"Extracted {len(result.results[0].content)} characters")
    except FileNotFoundError as e:
        print(f"File not found: {e}")
    except ParsingError as e:
        print(f"Failed to parse document: {e}")
    except OcrError as e:
        print(f"OCR processing failed: {e}")
    except XbergError as e:
        print(f"Extraction error: {e}")

    try:
        config: ExtractionConfig = ExtractionConfig()
        pdf_bytes: bytes = b"%PDF-1.4\n"
        result = await extract(
            ExtractInput(kind="bytes", bytes=pdf_bytes, mime_type="application/pdf", filename="document.pdf"),
            config,
        )
        print(f"Extracted: {result.results[0].content[:100]}")
    except ValidationError as e:
        print(f"Invalid configuration: {e}")
    except OcrError as e:
        print(f"OCR failed: {e}")
    except XbergError as e:
        print(f"Extraction failed: {e}")

asyncio.run(main())
```
