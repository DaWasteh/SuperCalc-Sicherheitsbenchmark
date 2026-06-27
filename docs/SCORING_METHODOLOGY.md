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

Raw model output is always stored next to the normalized parse. If the runtime exposes visible `reasoning_content` or inline `<think>...</think>` blocks, the tool also parses and matches that text separately for the non-scoring **Denken-vs-Sagen** diagnostic: unique true positives seen in thinking are compared with unique true positives reported in the final assistant output. This helps distinguish discovery failures from reporting/self-filtering failures, but it never changes the 0..100 benchmark score because not every model exposes reasoning and unstructured thinking can be undercounted.

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
- Optional Denken-vs-Sagen counts when visible reasoning is available: thinking true positives, final-output true positives, thinking-only true positives, output-only true positives, and thinking→output coverage.

Run 2 is not allowed to use hidden ground truth; it only receives the code and the model's own Run-1 answer.

## Archived scorecards & cross-run comparison

Each completed run is archived as a compact scorecard (`archive/<benchmark>/<family>__<quant>/<timestamp>.json`) so multiple models — and multiple quantizations of the same model — can be compared later without re-running them. The archive groups runs by **model family + quant**, both parsed from the llama.cpp model id / GGUF name (overridable via `--quant` or the GUI **Quant** field). If a model alias hides this information, only the identity fields are editable after the fact: double-click **Modell** or **Quant** in the GUI comparison grid, or change `modelFamily`/`quant` in the JSON scorecard manually; `groupKey` and the physical folder name are derived again when the archive is loaded.

By default, the **primary run** of each scorecard is used: Run 2 (self-validation) when present, otherwise Run 1. The comparison builder can also use `--run-view run1`, `--run-view run2`, or `--run-view delta` (Run2−Run1). This changes only the comparison perspective; it does not change any individual run's score.

Archive scorecards use schema v2. Older schema-v1 files still load: their `vulnerabilityCredit` map is converted in memory into v2 `vulnerabilityResults`. New scorecards add compact diagnostics from the run artifacts — finish reason, loop flag, parse mode/warning, prompt/request/response/reasoning character counts, per-run duration, duplicates, ignored-low-confidence counts, and per-vulnerability status — but still do **not** copy prompts or raw model responses into the archive.

The comparison view derives several read-only series from the archived scorecards:

- **Total score / selected metric:** the selected run view's score. When several runs of the same model + quant exist, the group can be summarized as **mean** (`average`), **median** (`median`), or the single **best** selected run.
- **Score distribution:** mean, median, sample standard deviation, IQR, optional 95% CI, min, and max.
- **Per-vulnerability credit:** for each ground-truth vulnerability, `1.0` if fully detected, `0.5` if partially detected, `0.0` if missed. Delta view stores Run2−Run1 credit per vulnerability.
- **Ground-truth metadata axes:** when local `ground_truth.json` is available, the report derives severity, CWE, category, and module labels for post-run filtering/aggregation. These labels are never included in model prompts. Use `--public-labels` when generating share-friendlier HTML to omit vulnerability titles/CWEs/modules.
- **Severity/category metrics:** Critical/High/Medium/Low recall, High+Critical recall, category scores (Memory Safety, Injection, Concurrency, Auth/Crypto, Numeric/DoS, File I/O), and CWE coverage.
- **Stability/reproducibility:** per-vulnerability stability across repeated runs, score IQR/CI, and run count filters.
- **Completion/parsing health:** parse success rate, loop rate, empty-output-with-reasoning rate, FP-per-finding, duplicate rate, ignored-low-confidence rate, response/reasoning sizes, and duration.
- **Run 1 vs Run 2:** score delta, FP reduction, TP retention, added TPs, and dropped TPs.

Generated `comparison.html`/`comparison.csv` reports live under `archive/_reports/` and are regenerated on demand, so they are not committed; the underlying scorecards are.
