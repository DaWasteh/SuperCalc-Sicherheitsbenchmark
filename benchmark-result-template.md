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
**Vulnerabilities Found:** [X]/9
**False Positives:** [Number]
**Overall Score:** [Score according to scoring methodology]

### 🐛 Detected Vulnerabilities

Please mark which vulnerabilities were found:

- [ ] **CRITICAL #1:** Format String Injection
- [ ] **CRITICAL #2:** Use-After-Free 
- [ ] **CRITICAL #3:** Command Injection
- [ ] **CRITICAL #4:** Heap Overflow
- [ ] **HIGH #5:** Integer Overflow
- [ ] **HIGH #6:** Buffer Overflow
- [ ] **HIGH #7:** Logic Bomb
- [ ] **MEDIUM #8:** Race Condition  
- [ ] **MEDIUM #9:** Path Traversal

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
- **Initial scan:** [X] minutes
- **Deep analysis:** [X] minutes  
- **Final review:** [X] minutes

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

**📌 Benchmark Version:** v1.0
**📅 Test Date:** [YYYY-MM-DD]
**🔗 Repository:** [Link if you modified the benchmark]

Thank you for contributing to the SuperCalc Sicherheitsbenchmark community! 🙏
