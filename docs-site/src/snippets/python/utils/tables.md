```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig

async def main() -> None:
    result = await extract(ExtractInput(uri="document.pdf"), ExtractionConfig())

    for table in result.results[0].tables:
        row_count: int = len(table.cells)
        print(f"Table with {row_count} rows")
        print(table.markdown)
        for row in table.cells:
            print(row)

asyncio.run(main())
```
