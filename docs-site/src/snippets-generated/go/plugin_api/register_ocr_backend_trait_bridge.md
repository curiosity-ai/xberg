---
id: fixture_go_register_ocr_backend_trait_bridge
language: go
target: go
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```go title="Go"
package main

import (
	xberg "xberg"
)

type testStub_register_ocr_backend_trait_bridge struct{}

func (testStub_register_ocr_backend_trait_bridge) ProcessImage(imageBytes []byte, config xberg.OcrConfig) (xberg.ExtractedDocument, error) { return xberg.ExtractedDocument{}, nil }
func (testStub_register_ocr_backend_trait_bridge) ProcessImageFile(path string, config xberg.OcrConfig) (xberg.ExtractedDocument, error) { return xberg.ExtractedDocument{}, nil }
func (testStub_register_ocr_backend_trait_bridge) SupportsLanguage(lang string) bool { return false }
func (testStub_register_ocr_backend_trait_bridge) BackendType() xberg.OcrBackendType { return xberg.OcrBackendTypeTesseract }
func (testStub_register_ocr_backend_trait_bridge) SupportedLanguages() []string { return nil }
func (testStub_register_ocr_backend_trait_bridge) SupportsTableDetection() bool { return false }
func (testStub_register_ocr_backend_trait_bridge) SupportsDocumentProcessing() bool { return false }
func (testStub_register_ocr_backend_trait_bridge) EmitsStructuredMarkdown() bool { return false }
func (testStub_register_ocr_backend_trait_bridge) ProcessDocument(path string, config xberg.OcrConfig) (xberg.ExtractedDocument, error) { return xberg.ExtractedDocument{}, nil }
func (testStub_register_ocr_backend_trait_bridge) Name() string { return "test-backend" }
func (testStub_register_ocr_backend_trait_bridge) Version() string { return "" }
func (testStub_register_ocr_backend_trait_bridge) Initialize() error { return nil }
func (testStub_register_ocr_backend_trait_bridge) Shutdown() error { return nil }
func (testStub_register_ocr_backend_trait_bridge) Description() string { return "" }
func (testStub_register_ocr_backend_trait_bridge) Author() string { return "" }

func main() {
	err := xberg.RegisterOcrBackend(testStub_register_ocr_backend_trait_bridge{})
	if err != nil {
		panic(err)
	}
}
```
