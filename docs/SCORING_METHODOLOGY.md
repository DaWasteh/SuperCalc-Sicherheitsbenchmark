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

## Archived scorecards & cross-run comparison

Each completed run is archived as a compact scorecard (`archive/<benchmark>/<family>__<quant>/<timestamp>.json`) so multiple models — and multiple quantizations of the same model — can be compared later without re-running them. The archive groups runs by **model family + quant**, both parsed from the llama.cpp model id / GGUF name (overridable via `--quant` or the GUI **Quant** field). If a model alias hides this information, the archived JSON scorecard may also be edited manually: change `modelFamily` and/or `quant`; `groupKey` and the physical folder name are derived again when the archive is loaded.

For comparison, the **primary run** of each scorecard is used: Run 2 (self-validation) when present, otherwise Run 1. This matches the headline score a single benchmark reports.

The comparison view derives two series from the archived scorecards:

- **Total score (bar chart):** the primary run's `ScorePercent`. When several runs of the same model + quant exist, the group is summarized as either the **mean** (`average`) or the single **best** primary-run score.
- **Per-vulnerability credit (radar / net chart):** for each ground-truth vulnerability, `1.0` if fully detected, `0.5` if partially detected, `0.0` if missed. All series share one vulnerability axis (the union of ids seen across the compared runs), so a model's strengths and blind spots are visible at a glance.

These are read-only views over the existing scoring output; they do not change how any individual run is scored. Generated `comparison.html`/`comparison.csv` reports live under `archive/_reports/` and are regenerated on demand, so they are not committed; the underlying scorecards are.
