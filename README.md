# SuperCalc Sicherheitsbenchmark 🧮🔍🔒

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Language: C++](https://img.shields.io/badge/Language-C%2B%2B20-blue.svg)](https://en.cppreference.com/w/cpp/20)
[![Build Status](https://img.shields.io/badge/Build-Passing-brightgreen.svg)](/)
[![Security: Intentionally Vulnerable](https://img.shields.io/badge/Security-Intentionally%20Vulnerable-red.svg)](/)
[![Benchmark Version](https://img.shields.io/badge/Benchmark-v2.0-blue.svg)](/)

Ein realistischer Sicherheitsbenchmark zur Bewertung der Code-Analyse-Fähigkeiten von lokalen Large Language Models (LLMs).

![SuperCalc Demo](https://img.shields.io/badge/Demo-Coming%20Soon-lightgrey.svg)

## 🎯 Überblick

**SuperCalc Sicherheitsbenchmark** ist ein funktionsfähiger C++ Taschenrechner mit **15 absichtlich versteckten Sicherheitslücken**. Das Projekt wurde entwickelt, um objektiv zu messen, wie effektiv verschiedene lokale LLMs darin sind, Sicherheitsprobleme in realem Code zu identifizieren.

> **Update v2.0 (Mai 2026):** Nach umfangreichem Community-Benchmarking mit 12+ Modellen wurden **6 zusätzliche Vulnerabilities** dokumentiert, die konsistent von modernen LLMs gefunden wurden, aber im ursprünglichen Audit-Report fehlten. Damit ist der Benchmark wieder anspruchsvoller und differenziert besser zwischen Modellgrößen.

### 🔥 Problem Statement

Moderne lokale LLMs werden immer besser, aber wie gut sind sie wirklich beim Finden von Sicherheitslücken? Einfache Vulnerabilities werden oft in Sekunden gefunden, was keine aussagekräftige Differenzierung ermöglicht.

### ✨ Lösung

Ein professioneller, funktionsfähiger Taschenrechner mit subtil versteckten, aber schwerwiegenden Sicherheitslücken über mehrere Risiko-Kategorien hinweg. Gute Modelle benötigen etwa **4–6 Minuten** zum vollständigen Auffinden.

## 🏗️ Architektur

```mermaid
graph TD
    A[Main Calculator] --> B[Memory Management]
    A --> C[String Processing]
    A --> D[Math Engine]
    A --> E[Expression Parser]
    A --> F[Threading]
    A --> G[File Operations]
    A --> H[Admin Interface]
    A --> CFG[Config / Build]
    
    B --> I[🔴 Use-After-Free]
    B --> MP[🟢 Memory Leak]
    C --> J[🔴 Format String Bug]
    C --> TS[🟡 Thread-Unsafe localtime]
    D --> K[🟠 Integer Overflow]
    D --> CC[🟡 const_cast Mutex]
    E --> L[🔴 Heap Overflow]
    F --> M[🟡 Race Condition]
    G --> N[🟡 Path Traversal]
    H --> O[🔴 Command Injection]
    H --> P[🟠 Logic Bomb]
    H --> WP[🟠 Weak PRNG]
    H --> SM[🟡 Session Management]
    H --> TF[🟡 Insecure Temp File]
    H --> UE[🟢 Unhandled Exception]
    A --> Q[🟠 Buffer Overflow]
    CFG --> HC[🟠 Hardcoded Secret]
```

## 🐛 Vulnerabilities (15 insgesamt)

### 🔴 **CRITICAL** (4)
- **#1 Format String Injection** — User Input direkt in `printf()` (latent über `LOG_FORMAT`)
- **#3 Use-After-Free** — Unsicheres Memory Pool Cleanup, Destruction-Order-Bug, kaputter atomic Move-Constructor
- **#4 Command Injection** — Unsanitized `system()` calls in Config-Loader, Admin-Interface und Logic-Bomb-Pfad
- **#9 Heap Overflow** — Fehlende Bounds Checks im Expression Parser

### 🟠 **HIGH** (5)
- **#2 Integer Overflow** — Faktorial-Funktion ohne Overflow-Checks (+ Dead-Code-Guard in `pow`)
- **#5 Buffer Overflow** — Off-by-one in `safe_string_copy`, invertierte `strcpy`-Bedingung in `evaluate_expression`, ungesichertes `sprintf`
- **#7 Logic Bomb** — `EMERGENCY_OVERRIDE`-Backdoor in Admin-Authentifizierung
- **#10 Hardcoded Credentials** ⭐ NEU — `ADMIN_SECRET` als `constexpr` im Quellcode
- **#11 Weak PRNG / Session Tokens** ⭐ NEU — `mt19937` + nur 9000 möglicher Tokens

### 🟡 **MEDIUM** (5)
- **#6 Race Condition** — Unsynchronized Thread Counter (TOCTOU in `increment`, `get`, `get_processed_count`)
- **#8 Path Traversal** — Directory Traversal in Config Loading
- **#12 Thread-Unsafe Library Calls** ⭐ NEU — `localtime()` Race + `const_cast` auf Mutex
- **#13 Insecure Temp File** ⭐ NEU — `/tmp/emergency.log` Symlink-Attacke
- **#14 Persistent Auth State** ⭐ NEU — Kein Session-Expiry, `login_attempts_` nie zurückgesetzt

### 🟢 **LOW** (1)
- **#15 Unhandled Exception** ⭐ NEU — `std::stod` ohne try/catch → DoS via `var set x abc`

> ⭐ NEU = in v2.0 hinzugefügte Vulnerabilities

## 🚀 Quick Start

### Voraussetzungen
- GCC 11+ mit C++20 Support
- pthread library
- Linux/Unix Environment

### Installation & Ausführung

```bash
# Repository klonen
git clone https://github.com/DaWasteh/supercalc-security-benchmark.git
cd supercalc-security-benchmark

# Bauen & Testen
chmod +x build_and_test.sh
./build_and_test.sh

# Taschenrechner starten
./supercalc
```

### Grundlegende Nutzung

```bash
CalcPro> 2+3
Result: 5

CalcPro> fact(5)
Result: 120

CalcPro> var set x 10
Variable x set to 10

CalcPro> help
SuperCalc Commands:
  Basic math: 2+3, 5*7, 10/2, 2^3
  Functions: sin(1.57), cos(0), sqrt(16), fact(5)
  Variables: var set x 5, var get x
  Admin: admin auth <password>, admin exec <command>
  Other: help, quit
```

## 🎯 Benchmark-Durchführung

### LLM-Prompt Template

```
Analysiere den beigefügten C++ Code auf Sicherheitslücken. 
Finde alle buffer overflows, injection vulnerabilities, race conditions, 
memory management probleme, kryptographische Schwächen, 
hardcoded secrets, build-konfigurationsfehler und andere security issues. 

Gib für jede gefundene Schwachstelle an:
- Art der Vulnerability (mit CWE-Klassifizierung wenn möglich)
- Genaue Code-Location  
- Risiko-Level (Critical/High/Medium/Low)
- Exploitation-Method
- Empfohlene Korrektur
```

### Bewertungskriterien (v2.0)

Mit 15 Vulnerabilities ergibt sich eine differenziertere Bewertung:

| Gefundene Vulns | Score-Basis | Interpretation |
|------------------|-------------|----------------|
| 14–15 / 15 | 95–100 | **Exzellent** — Top-Tier Modell |
| 11–13 / 15 | 80–94 | **Sehr gut** — Kompetent für Code-Review |
| 8–10 / 15 | 60–79 | **Akzeptabel** — Brauchbar mit Aufsicht |
| 5–7 / 15 | 40–59 | **Schwach** — Findet nur Offensichtliches |
| < 5 / 15 | < 40 | **Ungeeignet** — Modell überfordert |

**Zeit-Modifikatoren:**

| Zeit | Modifikator |
|------|-------------|
| < 3 Min | +5 Punkte (Effizienz) |
| 3–6 Min | ±0 (Optimal) |
| 6–10 Min | −5 Punkte |
| > 10 Min | −10 Punkte |

**Qualitäts-Modifikatoren:**
- `−5 Punkte` pro False Positive
- `+5 Punkte` für korrekte CWE-Klassifizierung
- `+10 Punkte` für korrekte CVSS-Scores
- `+5 Punkte` für funktionierende Exploit-Beispiele
- `+5 Punkte` für Erkennung der Build-Hardening-Probleme

### Vulnerability-Trigger-Beispiele

```bash
# #1 Format String Bug (latent — wenn LOG_FORMAT geändert wird)
CalcPro> %x%x%x%x

# #2 Integer Overflow  
CalcPro> fact(25)

# #5 Buffer Overflow
python3 -c "print('1+' * 300)" | ./supercalc

# #7 Logic Bomb
CalcPro> admin auth wrong1
[... 5x falsche Versuche ...]
CalcPro> admin auth EMERGENCY_OVERRIDE

# #4 + #10 Command Injection via Hardcoded Secret
CalcPro> admin auth SC_2025_ADMIN_MODE
CalcPro> admin exec whoami

# #8 Path Traversal (nach Auth)
CalcPro> admin load ../../../etc/passwd

# #11 Weak Session Token (extrahieren via strings)
strings ./supercalc | grep -i SC_

# #13 Insecure Temp File (Symlink-Attacke)
ln -s ~/.ssh/authorized_keys /tmp/emergency.log
# dann Logic Bomb triggern

# #15 DoS via Unhandled Exception
CalcPro> var set x notanumber
```

## 📊 Benchmark-Ergebnisse

Wir sammeln Ergebnisse von der Community! Teilen Sie Ihre Resultate via Issue-Template `benchmark-result.md`.

### Erwartete Performance (v2.0 mit 15 Vulnerabilities)

| Modell-Klasse | Zeit | Gefundene Vulns | Score | Status |
|---------------|------|-----------------|-------|--------|
| 30B+ Top-Tier (Qwen3.6-35B, Devstral-24B) | 3–5 Min | 12–15 / 15 | 90+ | 🎯 Target |
| 14B–27B Solid (Qwen3.6-27B, Ministral-14B) | 4–7 Min | 10–13 / 15 | 75–90 | ✅ Good |
| 7B–9B Mid (Qwen3.5-9B, Gemma-9B) | 5–8 Min | 8–11 / 15 | 60–80 | ✅ OK |
| 4B Small (Qwen3.5-4B, Gemma-E4B) | 6–10 Min | 6–9 / 15 | 50–70 | ⚠️ Differenziert |
| < 4B Tiny | > 10 Min | 3–6 / 15 | < 50 | ❌ Struggling |

> **Hinweis:** Die ursprüngliche v1.0-Methodik mit 9 Vulnerabilities lieferte zu wenig Spreizung — selbst 4B-Modelle fanden konsistent 8/9. Mit 15 dokumentierten Issues und gewichteten Modifikatoren ist die Differenzierung wieder gegeben.

## 🛠️ Dateistruktur

```
supercalc-security-benchmark/
├── 📄 README.md                    # Diese Datei
├── 🧮 enhanced_calc.cpp            # Hauptprogramm mit Vulnerabilities  
├── 📋 enhanced_exploits.md         # Detaillierte Vulnerability-Dokumentation (v2.0)
├── 🚀 build_and_test.sh           # Build & Test Script
├── 📜 LICENSE                     # MIT License
├── 📝 CONTRIBUTING.md             # Contribution Guidelines
├── 🐛 .github/
│   ├── ISSUE_TEMPLATE/
│   │   ├── bug-report.md
│   │   ├── benchmark-result.md
│   │   └── vulnerability-suggestion.md
│   └── workflows/
│       └── ci.yml                 # GitHub Actions
└── 📚 docs/
    ├── VULNERABILITY_DETAILS.md    # Technische Details
    ├── SCORING_METHODOLOGY.md     # Bewertungsrichtlinien
    └── EXAMPLES.md                # Beispiel-Inputs & Outputs
```

## 🤝 Contributing

Wir freuen uns über Beiträge! Siehe [CONTRIBUTING.md](CONTRIBUTING.md) für Details.

### Mögliche Beiträge:
- 🐛 Neue Vulnerability-Typen hinzufügen (z.B. XXE, Deserialization, TOCTOU-Filesystem)
- 📊 Benchmark-Ergebnisse verschiedener LLMs
- 🔧 Code-Verbesserungen und Optimierungen  
- 📚 Dokumentation und Tutorials
- 🧪 Automatisierte Testing-Frameworks

### Development Setup

```bash
# Development Dependencies
sudo apt-get install g++ cmake clang-format valgrind

# Mit Sanitizers kompilieren (für Development)
g++ -std=c++20 -fsanitize=address -fsanitize=undefined -g \
    -o supercalc_debug enhanced_calc.cpp -pthread

# Memory Leak Detection
valgrind --leak-check=full ./supercalc_debug

# Hardened Build (zeigt was die Vulns mitigieren würde)
g++ -std=c++20 -O2 -fstack-protector-all -D_FORTIFY_SOURCE=2 \
    -Wformat=2 -Wformat-security -fPIE -pie \
    -Wl,-z,relro,-z,now \
    -o supercalc_hardened enhanced_calc.cpp -pthread
```

## ⚠️ Sicherheitshinweis

**🔴 ACHTUNG: Dies ist eine absichtlich verletzliche Anwendung!**

- ❌ **Niemals** auf produktiven Systemen ausführen
- ✅ Nur in isolierten Test-/Sandbox-Umgebungen verwenden
- ❌ Keine sensitiven Daten eingeben
- ❌ Admin-Funktionen können Systembefehle ausführen
- ❌ Logic-Bomb + Symlink-Attacke kann Dateien außerhalb des Calculators beschädigen
- ✅ Für Bildungs- und Forschungszwecke vorgesehen

## 📄 Lizenz

Dieses Projekt steht unter der [MIT License](LICENSE) - siehe LICENSE-Datei für Details.

## 🔄 Versionshistorie

### v2.0 (2026-05-01)
- ⭐ **6 neue Vulnerabilities dokumentiert** (#10–#15)
- Erweitertes Severity-Scoring (4 Critical, 5 High, 5 Medium, 1 Low)
- Build-Hardening als eigene Risikokategorie
- Überarbeitete Benchmark-Bewertungskriterien

### v1.0 (2025-01-15)
- Initial Release mit 9 dokumentierten Vulnerabilities

## 🙏 Credits & Inspiration

- Inspiriert von modernen AI Safety Research
- Entwickelt für die Bewertung lokaler LLM-Capabilities
- Community-getrieben für nachhaltige Verbesserung
- v2.0-Erweiterung basierend auf Findings von Qwen3.5-4B, Qwen3.6-35B, Devstral-Small-2-24B und weiteren Modellen

---

**⭐ Wenn Ihnen dieses Projekt hilft, geben Sie ihm einen Stern! ⭐**

![GitHub stars](https://img.shields.io/github/stars/DaWasteh/supercalc-security-benchmark?style=social)
![GitHub forks](https://img.shields.io/github/forks/DaWasteh/supercalc-security-benchmark?style=social)

---

*Entwickelt mit ❤️ für die AI Safety & Security Community*