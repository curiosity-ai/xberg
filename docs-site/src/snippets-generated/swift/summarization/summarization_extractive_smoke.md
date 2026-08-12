---
id: fixture_swift_summarization_extractive_smoke
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

TextRank extractive summary over a multi-paragraph plain text document. Pure-Rust, deterministic, no external services required.

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"uri\":\"https://example.com/text/book_war_and_peace_1p.txt\"}", "{\"summarization\":{\"max_tokens\":80,\"strategy\":\"extractive\"}}")

```
