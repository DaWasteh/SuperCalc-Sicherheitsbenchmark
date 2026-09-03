# v0.7.5 Qwen3.8 Vulkan/HIP comparison

## Scope

This comparison ran `Qwen3.8-27B-Ridge-3.7bpw` through the complete SuperCalc Run 1, Run 2, and Run 3 truth-audit pipeline once on each llama.cpp backend. Both final truth audits parsed and validated successfully.

The comparison controls the physical GPU, model files, llama.cpp revision, context, sampling, speculative decoding, and benchmark profile. It is an observed pair of runs, not a statistical claim that either backend changes model quality.

## Controlled runtime identity

| Setting | Vulkan | HIP |
| --- | --- | --- |
| Physical GPU | AMD Radeon AI PRO R9700, 32 GB | AMD Radeon AI PRO R9700, 32 GB |
| Device mapping | `GGML_VK_VISIBLE_DEVICES=1` → `Vulkan0` | `HIP_VISIBLE_DEVICES=0` → `ROCm0` |
| llama.cpp | `b10760-0f3a71be1` | `b10760-0f3a71be1` |
| Context | 262,144 | 262,144 |
| Threads / batch threads | 12 / 16 | 12 / 16 |
| Batch / micro-batch | 1,024 / 1,024 | 1,024 / 1,024 |
| GPU layers | 999 | 999 |
| KV cache | `q4_0` K and V | `q4_0` K and V |
| Speculation | `draft-dflash,ngram-map-k4v` | `draft-dflash,ngram-map-k4v` |
| Sampling | seed 12345, temp 1.0, top-k 20, top-p 0.95, min-p 0, repeat penalty 1.0 | identical |
| Benchmark scoring | `official-v1` | `official-v1` |
| Truth audit | `always`, forced Run 2 | identical |
| Truth-audit prompt | `truth_audit_v2` | `truth_audit_v2` |

Run 2 was also the higher-scoring detection run on both backends, so forcing `run2` selected the same answer that `best` would have selected.

The `/props` payloads were identical except for llama-server's generated `media_marker`. The `/v1/models` payloads were identical except for their process-start `created` timestamps. Both reported `IQ2_M - 2.7 bpw`; that runtime metadata is authoritative even though the source filename contains `3.7bpw`.

### Immutable file identities

- Main GGUF SHA-256: `95580dbdaad579582ee898257116abc18d7f3625a00c16a15735d41444a09f5e`
- Draft GGUF SHA-256: `18a380efc9b7ed8d88677fc895f5c11ae170653434ee378f7348f715c14d0594`
- MMProj SHA-256: `52228402ce4823f10705d901813cd43ced71859524cf2d8bf83305ad6b7dcbc2`
- Vulkan server SHA-256: `4b78a8c6c46c7a2b0e5b3f17e019c03af02c371161c2a6f50f4a856f4b6a5b50`
- HIP server SHA-256: `1dbe70c33cf9a3eac410da91ad6978049073ccdc55a3e126340c4bf664819c66`

## Detection results

| Backend | Run | Score | Full TP | Partial TP | FP | Duplicates | Missed | Precision | Recall |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Vulkan | 1 | 42.00 | 10 | 0 | 0 | 3 | 10 | 100.00% | 50.00% |
| Vulkan | 2 | 46.00 | 11 | 0 | 0 | 3 | 9 | 100.00% | 55.00% |
| HIP | 1 | 32.00 | 8 | 0 | 1 | 3 | 12 | 88.89% | 40.00% |
| HIP | 2 | 34.50 | 8 | 3 | 2 | 2 | 9 | 73.08% | 47.50% |

Run 1 used byte-identical prompts and request JSON across Vulkan and HIP:

- Prompt SHA-256: `7580a28af5ce775e600f5c2b01d6184cf7fea9e1c3e618942ea735b8e7600567`
- Request SHA-256: `e0413c0dfc4ce46bc299429f26bc6ffb051df9a4e11dc10def8b5abd49853339`

Run 2 and Run 3 prompts necessarily diverged because each included its backend's preceding generated answer.

## Truth-audit results

| Metric | Vulkan | HIP |
| --- | ---: | ---: |
| Structurally valid | yes | yes |
| Validation errors | 0 | 0 |
| Accountability | 70.00 | 29.55 |
| Truth-audit accuracy | 95.00% | 65.00% |
| Miss admission | 88.89% | 88.89% |
| Overclaim rate | 11.11% | 11.11% |
| False-positive admission | 100.00%* | 50.00% |
| Quote fidelity | 91.67% | 91.67% |
| Contradictions | 1 | 7 |
| Evidence-laundering items | 1 | 1 |

\* The Vulkan audited run had zero scored false positives, so its admission rate is the vacuous 1.0 value. Its three extra findings were scored as duplicates and were still valid targets for explicit unsupported/duplicate admissions; they did not inflate the false-positive denominator.

## Runtime observations

| Run | Vulkan prompt t/s | HIP prompt t/s | Vulkan decode t/s | HIP decode t/s | Vulkan draft acceptance | HIP draft acceptance |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 658.18 | 778.30 | 41.61 | 37.06 | 41.53% | 41.39% |
| 2 | 621.35 | 699.78 | 33.63 | 27.28 | 32.49% | 28.68% |
| 3 | 612.34 | 685.98 | 32.78 | 43.31 | 32.30% | 51.61% |
| Weighted | 627.49 | 714.13 | 34.30 | 32.22 | — | — |

Total client-observed durations were 20:08.318 for Vulkan and 27:23.302 for HIP. This is not a like-for-like latency benchmark because HIP generated 50,506 completion tokens while Vulkan generated 38,502. HIP evaluated prompts faster in this pair; decode throughput varied by run and by speculative-acceptance behavior.

## Scoring decision

No backend-dependent scoring was introduced.

The same semantic scorer and hidden ground truth must evaluate both outputs. More importantly, two sequential Vulkan Run-1 calls on the same server used byte-identical request JSON and seed but produced different responses and scores (46 versus 42). This demonstrates meaningful within-backend generation variance under the selected speculative/sampling configuration. A single Vulkan/HIP pair therefore cannot isolate a causal backend-quality effect.

Backend identity is recorded as metadata for the two v0.7.5 scorecards. A causal backend study would require repeat cohorts, controlled server restarts/cache state, and multiple seeds; it should not alter answer-quality scoring.

## Reproduction artifacts

The release evidence bundle contains:

- exact server and benchmark commands;
- device-list, hardware, version, SHA-256, `/health`, `/props`, `/v1/models`, and `/metrics` captures;
- complete Vulkan and HIP run directories, including prompts, requests, reasoning, responses, reports, and `run.json`;
- server and CLI logs;
- `comparison.json`, the machine-readable source for this summary.

The repository scorecards are under:

- `archive/supercalc-v3/qwen3-8-27b-ridge-3-7bpw__IQ2_M/20260903-135420_qwen3-8-27b-ridge-3-7bpw.json` (Vulkan)
- `archive/supercalc-v3/qwen3-8-27b-ridge-3-7bpw__IQ2_M/20260903-141646_qwen3-8-27b-ridge-3-7bpw.json` (HIP)
