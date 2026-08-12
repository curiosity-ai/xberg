---
id: fixture_swift_summarization_abstractive_smoke
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

LLM-driven abstractive summary. Skipped automatically when XBERG_LLM_API_KEY (or OPENAI_API_KEY) is not set.

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/text/book_war_and_peace_1p.txt\"}", "{\"summarization\":{\"llm\":{\"max_tokens\":200,\"model\":\"openai/gpt-4o-mini\",\"temperature\":0.0},\"max_tokens\":150,\"strategy\":\"abstractive\"}}")

```
