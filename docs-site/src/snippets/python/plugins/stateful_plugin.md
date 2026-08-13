```python title="Python"
import threading
from xberg import ExtractedDocument, ExtractionConfig

class StatefulPlugin:
    def __init__(self):
        self.lock: threading.Lock = threading.Lock()
        self.call_count: int = 0
        self.cache: dict = {}

    def name(self) -> str:
        return "stateful-plugin"

    def version(self) -> str:
        return "1.0.0"

    def processing_stage(self) -> str:
        return "early"

    def process(self, result: ExtractedDocument, config: ExtractionConfig) -> None:
        with self.lock:
            self.call_count += 1
            self.cache["last_mime"] = result.mime_type

    def initialize(self) -> None:
        pass

    def shutdown(self) -> None:
        pass
```
