---
id: fixture_java_register_post_processor_trait_bridge
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

register_post_processor: trait bridge

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        class TestStubRegisterPostProcessorTraitBridge implements io.xberg.IPostProcessor {
    @Override
    public void process(io.xberg.ExtractedDocument result, io.xberg.ExtractionConfig config) {}
    @Override
    public String processing_stage() {
        return "null";
    }
    @Override
    public boolean should_process(io.xberg.ExtractedDocument result, io.xberg.ExtractionConfig config) {
        return false;
    }
    @Override
    public long estimated_duration_ms(io.xberg.ExtractedDocument result) {
        return 1;
    }
    @Override
    public int priority() {
        return 1;
    }
    @Override
    public String name() { return "test-processor"; }
    @Override
    public String version() {
        return "";
    }
    @Override
    public void initialize() {}
    @Override
    public void shutdown() {}
}

        Xberg.registerPostProcessor(new TestStubRegisterPostProcessorTraitBridge());
    }
}

```
