```python title="Document Structure Config (Python)"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig

async def main() -> None:
    # Enable document structure output
    config = ExtractionConfig(include_document_structure=True)

    result = await extract(ExtractInput(uri="document.pdf"), config)

    # Access the document tree
    document = result.results[0].document
    if document:
        for node in document.nodes:
            node_type = node.content.node_type
            text = getattr(node.content, "text", "")
            print(f"[{node_type}] {text[:80]}")

asyncio.run(main())
```
