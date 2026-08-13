```python title="Python"
import logging
from xberg import register_post_processor, ExtractedDocument, ExtractionConfig

logger = logging.getLogger(__name__)

class WordCountProcessor:
    def name(self) -> str:
        return "word_count"

    def version(self) -> str:
        return "1.0.0"

    def processing_stage(self) -> str:
        return "early"

    def process(self, result: ExtractedDocument, config: ExtractionConfig) -> None:
        word_count: int = len(result.content.split())
        logger.info(f"Word count: {word_count}")

    def should_process(self, result: ExtractedDocument, config: ExtractionConfig) -> bool:
        return bool(result.content)

    def initialize(self) -> None:
        pass

    def shutdown(self) -> None:
        pass

processor: WordCountProcessor = WordCountProcessor()
register_post_processor(processor)
```
