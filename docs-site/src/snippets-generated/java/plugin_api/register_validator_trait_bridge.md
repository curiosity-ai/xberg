---
id: fixture_java_register_validator_trait_bridge
language: java
target: java
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```java title="Java"
import io.xberg.Xberg.*;

public final class Example {
    public static void main(String[] args) throws Exception {
        class TestStubRegisterValidatorTraitBridge implements io.xberg.IValidator {
    @Override
    public void validate(io.xberg.ExtractedDocument result, io.xberg.ExtractionConfig config) {}
    @Override
    public boolean should_validate(io.xberg.ExtractedDocument result, io.xberg.ExtractionConfig config) {
        return false;
    }
    @Override
    public int priority() {
        return 1;
    }
    @Override
    public String name() { return "test-validator"; }
    @Override
    public String version() {
        return "";
    }
    @Override
    public void initialize() {}
    @Override
    public void shutdown() {}
}

        Xberg.registerValidator(new TestStubRegisterValidatorTraitBridge());
    }
}

```
