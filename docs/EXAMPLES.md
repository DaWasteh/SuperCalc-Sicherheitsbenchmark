# SuperCalc Validation Examples

These examples are for local validation of the intentionally vulnerable benchmark. Use an isolated test environment only.

## Non-destructive functionality smoke test

```text
2+3
5*7
fact(5)
quit
```

## Representative vulnerability triggers

| ID | Input / setup | Expected class |
| -- | ------------- | -------------- |
| SC-V3-002 | `fact(25)` | Integer overflow |
| SC-V3-005 | Expression longer than 1024 bytes | Stack buffer overflow path |
| SC-V3-007 | 6 failed auth attempts, then password containing `EMERGENCY_OVERRIDE` | Auth logic bomb |
| SC-V3-015 | `var set x abc` | Unhandled exception / DoS |
| SC-V3-020 | Empty or whitespace-only line | Integer divide-by-zero in routing |

For benchmark scoring, prefer the machine-readable hidden ground truth in `benchmarks/supercalc-v3/ground_truth.json` and the rules in `docs/SCORING_METHODOLOGY.md`.
