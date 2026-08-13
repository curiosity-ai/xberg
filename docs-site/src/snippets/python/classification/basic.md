```python title="Python"
import asyncio
from xberg import ExtractInput, extract, ExtractionConfig, PageClassificationConfig, LlmConfig

async def main() -> None:
    config = ExtractionConfig(
        page_classification=PageClassificationConfig(
            labels=["invoice", "contract", "id_document", "receipt"],
            multi_label=False,
            llm=LlmConfig(model="openai/gpt-4o-mini"),
        ),
    )
    result = await extract(ExtractInput(uri="packet.pdf"), config)
    for page in result.results[0].page_classifications or []:
        chosen = page.labels[0].label
        print(f"page {page.page_number}: {chosen}")

asyncio.run(main())
```
