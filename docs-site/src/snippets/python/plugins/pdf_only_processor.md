```python title="Python"
from xberg import ExtractedDocument, ExtractionConfig, register_post_processor

class PdfOnlyProcessor:
    def name(self) -> str:
        return "pdf-only-processor"

    def version(self) -> str:
        return "1.0.0"

    def processing_stage(self) -> str:
        return "early"

    def process(self, result: ExtractedDocument, config: ExtractionConfig) -> None:
        pass

    def should_process(self, result: ExtractedDocument, config: ExtractionConfig) -> bool:
        return result.mime_type == "application/pdf"

processor: PdfOnlyProcessor = PdfOnlyProcessor()
register_post_processor(processor)
```
