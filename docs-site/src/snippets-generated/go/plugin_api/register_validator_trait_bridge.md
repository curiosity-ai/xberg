---
id: fixture_go_register_validator_trait_bridge
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```go title="Go"
package main

import (
	xberg "xberg"
)

type testStub_register_validator_trait_bridge struct{}

func (testStub_register_validator_trait_bridge) Validate(resultArg xberg.ExtractedDocument, config xberg.ExtractionConfig) error { return nil }
func (testStub_register_validator_trait_bridge) ShouldValidate(result xberg.ExtractedDocument, config xberg.ExtractionConfig) bool { return false }
func (testStub_register_validator_trait_bridge) Priority() int32 { return 0 }
func (testStub_register_validator_trait_bridge) Name() string { return "" }
func (testStub_register_validator_trait_bridge) Version() string { return "" }
func (testStub_register_validator_trait_bridge) Initialize() error { return nil }
func (testStub_register_validator_trait_bridge) Shutdown() error { return nil }
func (testStub_register_validator_trait_bridge) Description() string { return "" }
func (testStub_register_validator_trait_bridge) Author() string { return "" }

func main() {
	err := xberg.RegisterValidator(testStub_register_validator_trait_bridge{})
	if err != nil {
		panic(err)
	}
}
```
