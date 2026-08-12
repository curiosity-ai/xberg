---
id: fixture_swift_format_xlsx
language: swift
target: swift
level: typecheck
requires: []
side_effect: server
---

XLSX spreadsheet extraction using extract

```swift title="Swift"
import Xberg

_ = try await Xberg.extract("{\"kind\":\"uri\",\"mime_type\":\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\",\"uri\":\"https://example.com/xlsx/stanley_cups.xlsx\"}", "{}")

```
