```python title="Element-Based Output (Python)"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, ElementType

async def main() -> None:
    # Configure element-based output
    config = ExtractionConfig(result_format="element_based")

    # Extract document
    result = await extract(ExtractInput(uri="document.pdf"), config)
    elements = result.results[0].elements or []

    # Access elements
    for element in elements:
        print(f"Type: {element.element_type}")
        print(f"Text: {element.text[:100]}")

        if element.metadata.page_number:
            print(f"Page: {element.metadata.page_number}")

        if element.metadata.coordinates:
            coords = element.metadata.coordinates
            print(f"Coords: ({coords.x0}, {coords.y0}) - ({coords.x1}, {coords.y1})")

        print("---")

    # Filter by element type
    titles = [e for e in elements if e.element_type == ElementType.TITLE]
    for title in titles:
        level = title.metadata.additional.get("level", "unknown")
        print(f"[{level}] {title.text}")

asyncio.run(main())
```
