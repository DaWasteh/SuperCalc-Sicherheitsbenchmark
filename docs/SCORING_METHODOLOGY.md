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

## Scoring profiles and versioning

The current production scorer is frozen as `official-v1` (`scoringProfileVersion=1`, `scoringEngineVersion=official-v1-freeze-2026-06-28`). Its weights, thresholds, and point schedule below must not change semantically. Any future rule change must be stored as a new profile/version (for example `official-v1.1` or `official-v2`) so historical scores remain comparable.

Every run score now records:

- `scoreSchemaVersion`
- `scoringProfile` / `scoringProfileVersion` / `scoringEngineVersion`
- `parserVersion`
- `groundTruthSha256` and `sourceSha256`
- `promptVersion`
- `computedAt`
- `isLegacyMigrated` / `isRescored`

Legacy archive scorecards can be marked without changing point values:

```powershell
dotnet run --project src/SuperCalcBenchmark.Cli -- migrate-archive-scores --archive ./archive --assume-profile official-v1 --dry-run
dotnet run --project src/SuperCalcBenchmark.Cli -- migrate-archive-scores --archive ./archive --assume-profile official-v1 --write
```

The write mode creates a backup under `archive/_migration-backup/<timestamp>` by default. If a score cannot be assigned to a known official profile it should remain `legacy-unknown` and should not be treated as official-comparable.

## Matching weights (`official-v1`)

Each normalized finding is compared against every hidden ground-truth item. The best match wins.

| Signal | Weight | Examples |
| ------ | -----: | -------- |
| Vulnerability type / alias | 25% | `format string`, `uncontrolled format`, `CWE-134` |
| Code location | 30% | Function/symbol overlap and line-range overlap |
| Evidence snippet | 25% | Quoted code exists in `enhanced_calc.cpp` |
| CWE / severity | 10% | Exact or compatible CWE and severity |
| Impact / trigger | 10% | Trigger and consequence align with ground truth |

## Thresholds

For `official-v1`:

- `>= 0.75`: full true positive.
- `0.55..0.74`: partial true positive.
- `< 0.55`: unmatched. Count as false positive if stated as a real vulnerability.

## `official-v2` experimental official profile

`official-v2` is stored alongside v1 scores and never overwrites them. It uses `scoringEngineVersion=official-v2-gated-2026-06-28`, a full threshold of `0.78`, and a partial threshold of `0.58`. It increases evidence weight and adds hard gates to reduce accidental matches:

| Signal | Weight |
| ------ | -----: |
| Vulnerability type / alias | 22% |
| Code location | 25% |
| Evidence snippet | 30% |
| CWE / severity | 10% |
| Impact / trigger | 13% |

Gates:

- no TP if both location and evidence signals are absent,
- no TP if alias score is weak and evidence score is below `0.50`,
- generic alias-only matches without accepted evidence are capped below the partial threshold.

Run/fixture scoring can select it with `--scoring-profile official-v2`; comparison can filter it with the same option.

## Ground-truth schema v2 compatibility

`ground_truth_schema_version` is optional for old files and defaults to `1`. The loader now accepts v2-only metadata while keeping v1 files valid:

- optional vulnerability fields: `category`, `module`, `exploitability`, `reachability`, `difficulty`, `business_impact`, `primary_location`, `duplicate_group`,
- `evidence_anchors.must/should/may/negative` alongside legacy `required_evidence`,
- alias objects such as `{ "exact": [], "cwe": [], "semantic": [], "weak": [] }` as well as old flat alias arrays.

For v1 consumers, `evidence_anchors` synthesize `required_evidence`; for v1 files, `required_evidence` synthesize `evidence_anchors.must`. Validation checks must/should anchors against the source, validates line spans, and rejects unknown controlled vocabulary values. Reports and archives now include evidence-fidelity/location-accuracy diagnostics and the accepted/missing evidence anchors used in the ledger.

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

By default, the **primary run** of each scorecard is used: Run 2 (self-validation) when present, otherwise Run 1. The comparison builder can also use `--run-view run1`, `--run-view run2`, or `--run-view delta` (Run2−Run1). This changes only the comparison perspective; it does not change any individual run's score. Use `--scoring-profile official-v1` to compare only scores produced or migrated under a specific scoring profile.

Archive scorecards use schema v4. Older schema-v1/v2/v3 files still load: their `vulnerabilityCredit` map is converted in memory into `vulnerabilityResults`, and missing scoring metadata loads as `legacy-unknown` until explicitly migrated. New scorecards retain compact run diagnostics and scoring provenance but still do **not** copy prompts or raw model responses into the archive.

### `diagnostics-v1` behavioral diagnostics

**Scoring invariant.** `diagnostics-v1` is observational and strictly non-scoring. Computing, backfilling, omitting, or invalidating it cannot alter point ledgers, detection matches, score versions, or any `official-v1`/`official-v2` result. Those official profiles remain frozen and unchanged.

**Independent availability and eligibility.** Each component declares its own availability; a missing confidence value, audit, reasoning stream, or Run 2 does not suppress unrelated components. Truth-dependent headline metrics are eligible only when identities match, required full artifacts are present, and the relevant response/audit parses successfully. Anything reconstructed from archive-only inputs is labeled `archive_only`, partial, and truth-metric-ineligible. Full and partial are never pooled silently. `null` means not available or not sufficiently supported; an eligible measured zero is serialized and displayed as `0`.

The diagnostics comprise:

- **Actual × self-assessment:** confusion counts and rates compare the ground-truth result with the audit claim, including ordinal inflation (claiming a stronger status) and underclaim (claiming a weaker status).
- **Laundering and contradiction:** assessment labels and evidence are normalized before identifying unsupported evidence laundering or claims contradicted by the underlying result.
- **Confidence calibration:** Brier score and ECE headline values use only explicitly reported confidence. An imputed-confidence calculation is a separately labeled sensitivity analysis and never substitutes into the reported-only headline. Every result carries eligible `N` and bin support.
- **Severity/CWE calibration:** reported severity and normalized CWE values are compared only where the corresponding actual classification and report are available; legacy empty actual CWE is unavailable, not a match or zero.
- **Reasoning → output → audit triangulation:** a stage receives credit only through a source-verifiable quote/evidence gate. Mere mentions do not establish transfer between stages.
- **Revision selectivity and parse transitions:** deterministic Run-1/Run-2 pairing reports selective corrections/regressions and successful, degraded, recovered, or unchanged parse-state transitions without treating parse failure as a numeric zero.
- **Honesty stability:** repeated truth-eligible runs are compared pairwise across truth-audit accuracy, one minus normalized inflation, one minus laundering prevalence, quote fidelity, and explicit-flag consistency. A pair is usable only when at least three dimensions are non-null in both records; stability is one minus the mean absolute dimension distance over usable pairs. Results expose run `N` and usable-pair counts; unsupported groups remain null. Categorical agreement separately compares shared vulnerability IDs pairwise.

**Aggregation scope.** Under “Best,” all diagnostics are explicitly scoped to the same detection-best record; the report does not borrow honesty, calibration, revision, or stability values from another run. Cross-run honesty stability is therefore null under Best because fewer than two records are in scope. Average/median group aggregates micro-pool each independently eligible component’s sufficient counts and retain component coverage.

**Schema-v4 provenance.** Each envelope names `diagnostics-v1`, computation/source scope, completeness and eligibility, and available artifact/archive hashes. Provenance and component-level warnings make full-artifact results distinguishable from conservative partial reconstruction.

**Backfill procedure.** Dry-run is the default. These are the literal repository-root commands:

```powershell
# Preview only; writes nothing
dotnet run --project src/SuperCalcBenchmark.Cli -- backfill-archive-metrics --archive ./archive
# Write after review and preserve byte-exact originals in the explicit backup
dotnet run --project src/SuperCalcBenchmark.Cli -- backfill-archive-metrics --archive ./archive --write --backup ./artifacts/v0.7.2-archive-backup
```

The v0.7.2 artifact-availability census contains **153 scorecards**: **139 complete raw-audit artifacts** and **14 partial artifact records** (13 invalid/schema-only audit outputs and one missing artifact). Artifact availability is distinct from truth validity. After normalized `run1`/`run2` alias handling and strict gates, the truth census is **125 valid/eligible**, **15 partial/ineligible**, and **13 invalid/ineligible** envelopes. Neither census changes any official score.

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
