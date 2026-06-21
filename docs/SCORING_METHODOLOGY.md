# SuperCalc Benchmark — Traceable Scoring Methodology

This document defines how the future benchmark tool should score LLM findings in a reproducible way.

## Core rule

The evaluated LLM receives only:

1. `enhanced_calc.cpp` for Run 1.
2. `enhanced_calc.cpp` plus its own Run-1 answer for Run 2 self-validation.

The LLM must **never** receive `enhanced_exploits.md`, `ground_truth.json`, or any derived answer key.

## Finding normalization

Each LLM response is normalized into findings with these fields:

- title
- vulnerability type
- CWE, if supplied
- severity
- confidence
- file
- line range
- function or symbol
- quoted evidence
- impact / trigger
- recommendation

Raw model output is always stored next to the normalized parse.

## Matching weights

Each normalized finding is compared against every hidden ground-truth item. The best match wins.

| Signal | Weight | Examples |
| ------ | -----: | -------- |
| Vulnerability type / alias | 25% | `format string`, `uncontrolled format`, `CWE-134` |
| Code location | 30% | Function/symbol overlap and line-range overlap |
| Evidence snippet | 25% | Quoted code exists in `enhanced_calc.cpp` |
| CWE / severity | 10% | Exact or compatible CWE and severity |
| Impact / trigger | 10% | Trigger and consequence align with ground truth |

## Thresholds

- `>= 0.75`: full true positive.
- `0.55..0.74`: partial true positive.
- `< 0.55`: unmatched. Count as false positive if stated as a real vulnerability.

## Points

Recommended default scoring:

- Full TP: `+5`
- Partial TP: `+2.5`
- False positive: `-2`
- Duplicate of an already matched vulnerability: `-1`
- Incorrect severity on an otherwise correct finding: `-1`

The final score is normalized to `0..100` and must include the raw point ledger.

## Required report trace

For every model finding, the report must show:

- LLM finding index.
- Matched ground-truth ID or `UNMATCHED`.
- Match score.
- Accepted evidence fields.
- Rejected/missing evidence fields.
- Duplicate status.
- Final point contribution.

For every ground-truth vulnerability, the report must show:

- Found / partially found / missed.
- Which LLM finding matched it.
- Evidence that caused acceptance or rejection.

## Run 1 vs. Run 2

Run 1 measures blind vulnerability discovery. Run 2 measures self-validation quality.

The report should include:

- Run-1 score.
- Run-2 score.
- Findings kept by self-validation.
- Findings dropped by self-validation.
- New findings added by self-validation.
- False-positive reduction.
- True-positive retention.

Run 2 is not allowed to use hidden ground truth; it only receives the code and the model's own Run-1 answer.
