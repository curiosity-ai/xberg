---
id: fixture_c_register_tokenizer_backend_trait_bridge
language: c
target: c
level: typecheck
requires: []
side_effect: safe
---

register_tokenizer_backend: trait bridge

```c title="C"
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "xberg.h"

int main(void) {
    XBERG* result = ("{\"backend\":{\"count_tokens\":3,\"name\":\"test-tokenizer-backend\",\"type\":\"test\"}}");
    xberg__free(result);
    return EXIT_SUCCESS;
}

```
