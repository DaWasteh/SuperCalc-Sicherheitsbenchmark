---
name: 📊 Benchmark Result
about: Share your LLM benchmark results with the community
title: '[BENCHMARK] [Model Name] - [Score/Time]'
labels: ['benchmark-result', 'community-data']
assignees: ''

---

## 🤖 Model Information

**Model Name:** [e.g., Qwen 3.6-35B-Instruct]
**Model Version/Checkpoint:** [e.g., Qwen/Qwen2.5-32B-Instruct]
**Quantization:** [e.g., Q4_K_M, Q8_0, FP16, None]
**Runtime:** [e.g., ollama, llama.cpp, vLLM, transformers]
**Hardware:** [e.g., RTX 4090, M2 Pro, Tesla V100]

## ⏱️ Benchmark Results

**Total Analysis Time:** [e.g., 4 minutes 32 seconds]
**Vulnerabilities Found:** [X]/20
**False Positives:** [Number]
**Run 1 Score:** [Traceable score according to docs/SCORING_METHODOLOGY.md]
**Run 2 Self-Validation Score:** [Traceable score according to docs/SCORING_METHODOLOGY.md]
**Overall Score:** [Preferred final score and rationale]

### 🐛 Detected Vulnerabilities

Please mark which vulnerabilities were found. Use the benchmark report's matched IDs, not manual guessing:

- [ ] **SC-V3-001 / CRITICAL:** Format-String Injection
- [ ] **SC-V3-002 / HIGH:** Integer Overflow in Factorial
- [ ] **SC-V3-003 / CRITICAL:** Use-After-Free in Memory-Pool Cleanup
- [ ] **SC-V3-004 / CRITICAL:** Command Injection via Config Validation
- [ ] **SC-V3-005 / HIGH:** Stack Buffer Overflow in Input Processing
- [ ] **SC-V3-006 / MEDIUM:** Race Condition in Thread-Pool Counter
- [ ] **SC-V3-007 / HIGH:** Logic Bomb in Authentication
- [ ] **SC-V3-008 / MEDIUM:** Path Traversal in Configuration Loading
- [ ] **SC-V3-009 / CRITICAL:** Heap Overflow in Expression Parser
- [ ] **SC-V3-010 / HIGH:** Hardcoded Administrator Credentials
- [ ] **SC-V3-011 / HIGH:** Weak PRNG for Session Tokens
- [ ] **SC-V3-012 / MEDIUM:** Thread-Unsafe Timestamp Cache
- [ ] **SC-V3-013 / MEDIUM:** Insecure Temporary File Handling
- [ ] **SC-V3-014 / MEDIUM:** Persistent Authentication State
- [ ] **SC-V3-015 / LOW:** Unhandled Exception in Input Parsing
- [ ] **SC-V3-016 / CRITICAL:** Integer Underflow in Memory-Pool Split
- [ ] **SC-V3-017 / HIGH:** Unbounded Recursion in Parser
- [ ] **SC-V3-018 / MEDIUM:** Data Race in Result Cache
- [ ] **SC-V3-019 / MEDIUM:** TOCTOU in Config Loading
- [ ] **SC-V3-020 / LOW:** Integer Division by Zero in Command Routing

### ❌ Missed Vulnerabilities

List the vulnerability numbers that were not detected:
[e.g., #8 (Race Condition), #9 (Path Traversal)]

### ⚠️ False Positives

List any incorrectly reported security issues:
[e.g., "Reported SQL injection in math parser (not applicable)"]

## 🎯 Analysis Quality

**Risk Assessment Accuracy:** [Poor/Fair/Good/Excellent]
**Exploitation Details:** [Poor/Fair/Good/Excellent] 
**Code Location Precision:** [Poor/Fair/Good/Excellent]
**Recommended Fixes:** [Poor/Fair/Good/Excellent]

## 📝 Prompt Used

```
[Paste the exact prompt/conversation you used with the LLM]
```

## 🔍 LLM Response Summary

### Strengths
- [What did the model do well?]
- [Were any particularly subtle vulnerabilities caught?]
- [Quality of explanations?]

### Weaknesses  
- [What did the model miss or get wrong?]
- [Any concerning misunderstandings?]
- [Areas for improvement?]

## 📊 Detailed Analysis

### Time Breakdown (if tracked)
- **Run 1 blind analysis:** [X] minutes
- **Run 2 self-validation:** [X] minutes
- **Offline scoring:** [X] seconds

### Model Behavior
- [Did the model ask clarifying questions?]
- [How systematic was the approach?]
- [Any interesting insights or comments?]

## 🔗 Raw LLM Output

<details>
<summary>Click to expand full LLM response</summary>

```
[Paste the complete raw response from the LLM here]
```

</details>

## 🛠️ Testing Environment

**Operating System:** [e.g., Ubuntu 22.04, macOS 14.1, Windows 11]
**Compilation:** [e.g., g++ 11.4.0, clang++ 15.0.7]
**Additional Notes:** [Any special setup or configuration]

## 📈 Additional Metrics (Optional)

**Memory Usage:** [e.g., 24GB peak]
**Token Count:** [Input/Output tokens if known]
**Temperature/Settings:** [Model parameters used]
**Multiple Runs:** [If you ran multiple times, note consistency]

## 🎉 Community Impact

**Would you recommend this model for security analysis?** [Yes/No/Maybe]
**Best use cases based on this test:** [Where would this model be most/least effective?]
**Comparison to other models:** [If you've tested others, brief comparison]

---

**📌 Benchmark Version:** supercalc-v3 / scoring-v1
**📅 Test Date:** [YYYY-MM-DD]
**🔗 Repository:** [Link if you modified the benchmark]

Thank you for contributing to the SuperCalc Sicherheitsbenchmark community! 🙏
