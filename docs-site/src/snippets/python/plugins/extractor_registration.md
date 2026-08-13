```python title="Python"
from xberg import register_document_extractor, ExtractInput, ExtractionConfig, ExtractedDocument

class CustomExtractor:
    def name(self) -> str:
        return "custom"

    def version(self) -> str:
        return "1.0.0"

    def supported_mime_types(self) -> list[str]:
        return ["application/x-custom"]

    def extract(self, input: ExtractInput, config: ExtractionConfig) -> dict:
        content = input.bytes.decode("utf-8") if input.bytes else ""
        return {"content": content, "mime_type": "application/x-custom"}

extractor = CustomExtractor()
register_document_extractor(extractor)
print("Extractor registered")
```
