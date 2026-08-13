```python title="Python"
from xberg import ExtractInput, ExtractionConfig

def test_custom_extractor() -> None:
    extractor = CustomJsonExtractor()
    json_data: bytes = b'{"message": "Hello, world!"}'
    input = ExtractInput(kind="bytes", bytes=json_data, mime_type="application/json")
    config = ExtractionConfig()
    result: dict = extractor.extract(input, config)
    assert "Hello, world!" in result["content"]
    assert result["mime_type"] == "application/json"
```
