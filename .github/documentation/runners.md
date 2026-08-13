# Custom GitHub Actions Runners

Almost all CI runs on GitHub-hosted runners (`ubuntu-latest`, `ubuntu-24.04-arm`). Only one
self-hosted runner remains, because GitHub offers no hosted equivalent on this plan.

## Available Runners

| Runner Label    | Architecture | Notes                                                     |
| --------------- | ------------ | --------------------------------------------------------- |
| `runner-gpu-l4` | x86_64 + L4  | NVIDIA L4 GPU. `ci-gpu.yaml` only, `workflow_dispatch` only |

The pool scales to zero, so it costs nothing when idle.

## Retired Runners

`runner-small`, `runner-medium-arm64`, `runner-large`, `runner-large-arm64`,
`runner-large-spot` and `runner-medium-arm64-spot` were removed. They served 55 hours of CI a
month but consumed 1,204 node-hours across 1,621 nodes, because each runner pod requested
4–12 vCPU and pinned one pod per host — so every job provisioned a fresh node that pulled its
whole toolchain over the network.

Use `ubuntu-latest` for x86_64 and `ubuntu-24.04-arm` for arm64 instead.

`runner-medium` still exists but is reserved for `sceptre`'s benchmark provenance (ADR 0035);
do not add jobs to it from this repo.

## Benchmarks

Benchmark figures produced before the migration were measured on dedicated non-spot hardware
and are not directly comparable with numbers from shared hosted runners. Re-baseline before
comparing.
