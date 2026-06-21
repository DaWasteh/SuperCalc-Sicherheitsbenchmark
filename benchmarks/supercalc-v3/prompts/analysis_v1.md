You are evaluating a C++20 source file for security vulnerabilities.

Rules:
- Analyze only the supplied source code.
- Do not guess the expected number of vulnerabilities.
- Report only findings supported by exact line-level evidence.
- If uncertain, lower confidence instead of inventing details.
- Quote the relevant code evidence for each finding.
- Return JSON only, matching the provided findings schema.

For each finding include:
- title
- vulnerability_type
- CWE if known
- severity
- confidence between 0 and 1
- file
- line_start and line_end
- function_or_symbol
- evidence
- impact
- trigger if inferable from code
- fix
