```python title="Python"
import logging
from xberg import ExtractInput, ExtractionConfig

logger = logging.getLogger(__name__)

class MyPlugin:
    def name(self) -> str:
        return "my-plugin"

    def version(self) -> str:
        return "1.0.0"

    def supported_mime_types(self) -> list[str]:
        return ["application/x-custom"]

    def initialize(self) -> None:
        logger.info(f"Initializing plugin: {self.name()}")

    def shutdown(self) -> None:
        logger.info(f"Shutting down plugin: {self.name()}")

    def extract(self, input: ExtractInput, config: ExtractionConfig) -> dict:
        logger.info(f"Extracting {input.mime_type} ({len(input.bytes or b'')} bytes)")
        result: dict = {"content": "", "mime_type": input.mime_type or "application/x-custom"}
        if not result["content"]:
            logger.warning("Extraction resulted in empty content")
        return result
```
