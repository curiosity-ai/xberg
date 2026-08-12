---
id: fixture_swift_config_llm_structured_extraction
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

Tests structured extraction via liter-llm with JSON schema

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/pdf/fake_memo.pdf\"}", "{\"structured_extraction\":{\"llm\":{\"model\":\"openai/gpt-4o\"},\"schema\":{\"properties\":{\"date\":{\"type\":\"string\"},\"summary\":{\"type\":\"string\"},\"title\":{\"type\":\"string\"}},\"required\":[\"title\"],\"type\":\"object\"},\"schema_name\":\"memo_data\"}}")

```
