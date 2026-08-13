---
id: fixture_java_register_ocr_backend_trait_bridge
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        class TestStubRegisterOcrBackendTraitBridge implements io.xberg.IOcrBackend {
    @Override
    public io.xberg.ExtractedDocument process_image(byte[] imageBytes, io.xberg.OcrConfig config) {
        return null;
    }
    @Override
    public io.xberg.ExtractedDocument process_image_file(java.nio.file.Path path, io.xberg.OcrConfig config) {
        return null;
    }
    @Override
    public boolean supports_language(String lang) {
        return false;
    }
    @Override
    public String backend_type() {
        return "null";
    }
    @Override
    public java.util.List<String> supported_languages() {
        return new java.util.ArrayList<>();
    }
    @Override
    public boolean supports_table_detection() {
        return false;
    }
    @Override
    public boolean supports_document_processing() {
        return false;
    }
    @Override
    public boolean emits_structured_markdown() {
        return false;
    }
    @Override
    public io.xberg.ExtractedDocument process_document(java.nio.file.Path path, io.xberg.OcrConfig config) {
        return null;
    }
    @Override
    public String name() { return "test-backend"; }
    @Override
    public String version() {
        return "";
    }
    @Override
    public void initialize() {}
    @Override
    public void shutdown() {}
}

        Xberg.registerOcrBackend(new TestStubRegisterOcrBackendTraitBridge());
    }
}

```
