```python title="Python"
from xberg import (
    ExtractionConfig, RedactionConfig, RedactionTerm, RedactionPattern,
)

config = ExtractionConfig(
    redaction=RedactionConfig(
        strategy="token_replace",
        custom_terms=[
            RedactionTerm(label="Project", value="Project Polaris", case_sensitive=False),
            RedactionTerm(label="Employee", value="EMP-7421", case_sensitive=True),
        ],
        custom_patterns=[
            RedactionPattern(label="InternalId", pattern=r"INT-\d{6}", case_sensitive=False),
        ],
    ),
)
```
