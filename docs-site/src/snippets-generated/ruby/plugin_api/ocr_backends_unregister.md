---
id: fixture_ruby_ocr_backends_unregister
language: ruby
target: ruby
level: typecheck
requires: []
side_effect: safe
---

Unregister nonexistent OCR backend gracefully

```ruby title="Ruby"
require "xberg"
Xberg.unregister_ocr_backend('nonexistent-backend-xyz')

```
