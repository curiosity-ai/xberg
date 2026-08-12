```python title="Python"
import asyncio
import json
from xberg import ExtractInput, extract, ExtractionConfig, StructuredExtractionConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        structured_extraction=StructuredExtractionConfig(
            schema=json.dumps({
                "type": "object",
                "properties": {
                    "title": {"type": "string"},
                    "authors": {"type": "array", "items": {"type": "string"}},
                    "date": {"type": "string"},
                },
                "required": ["title", "authors", "date"],
                "additionalProperties": False,
            }),
            schema_name="paper",
            llm=LlmConfig(model="openai/gpt-4o-mini"),
            strict=True,
        ),
    )
    result = await extract(ExtractInput(uri="paper.pdf"), config)
    print(result.results[0].structured_output)
    # {"title": "...", "authors": ["..."], "date": "..."}

asyncio.run(main())
```
