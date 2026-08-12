```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, PdfConfig, HierarchyConfig

async def main() -> None:
    config: ExtractionConfig = ExtractionConfig(
        pdf_options=PdfConfig(
            extract_metadata=True,
            hierarchy=HierarchyConfig(
                enabled=True,
                k_clusters=6,
                include_bbox=True,
            )
        )
    )

    result = await extract(ExtractInput(uri="document.pdf"), config)

    # Access hierarchy information
    for page in result.results[0].pages or []:
        print(f"Page {page.page_number}:")
        print(f"  Content: {page.content[:100]}...")

asyncio.run(main())
```
