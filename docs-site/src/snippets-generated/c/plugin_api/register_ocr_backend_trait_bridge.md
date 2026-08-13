---
id: fixture_c_register_ocr_backend_trait_bridge
language: c
target: c
level: typecheck
requires: []
side_effect: safe
---

register_ocr_backend: trait bridge

```c title="C"
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "xberg.h"

int main(void) {
    XBERG* result = ("{\"backend\":{\"name\":\"test-backend\",\"type\":\"test\"}}");
    xberg__free(result);
    return EXIT_SUCCESS;
}

```
