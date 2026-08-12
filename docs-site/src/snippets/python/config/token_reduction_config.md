```python title="Python"
from xberg import ExtractionConfig, TokenReductionOptions

config: ExtractionConfig = ExtractionConfig(
    token_reduction=TokenReductionOptions(
        mode="moderate",
        preserve_important_words=True,
    )
)
```
