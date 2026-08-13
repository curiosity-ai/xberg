---
id: fixture_c_register_validator_trait_bridge
language: c
target: c
level: typecheck
requires: []
side_effect: safe
---

register_validator: trait bridge

```c title="C"
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "xberg.h"

int main(void) {
    XBERG* result = ("{\"validator\":{\"name\":\"test-validator\",\"type\":\"test\"}}");
    xberg__free(result);
    return EXIT_SUCCESS;
}

```
