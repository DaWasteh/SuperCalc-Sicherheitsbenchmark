# Contributing to SuperCalc Sicherheitsbenchmark 🤝

Vielen Dank für Ihr Interesse an der Verbesserung des SuperCalc Sicherheitsbenchmarks! Wir freuen uns über Beiträge aus der Community.

## 🎯 Projektmission

Unser Ziel ist es, einen aussagekräftigen, objektiven Benchmark für die Sicherheitsanalyse-Fähigkeiten von lokalen LLMs zu entwickeln und zu pflegen.

## 🚀 Arten von Beiträgen

### 🐛 Neue Vulnerabilities
- Zusätzliche Vulnerability-Typen (CSRF, XXE, Deserialization, etc.)
- Subtilere Varianten bestehender Bugs  
- Platform-spezifische Sicherheitslücken
- Crypto/Protocol-Implementation Issues

### 📊 Benchmark-Daten
- Testergebnisse mit verschiedenen LLMs
- Performance-Metriken und Zeitvergleiche
- False-Positive/Negative Analysen
- Modell-spezifische Schwächen/Stärken

### 🔧 Code-Verbesserungen
- Build-System Optimierungen
- Cross-Platform Kompatibilität  
- Performance Verbesserungen
- Code-Qualität und Wartbarkeit

### 📚 Dokumentation
- Tutorial-Videos und Guides
- Übersetzungen in andere Sprachen
- API-Dokumentation und Beispiele
- Best-Practice Guides

## 📋 Contribution Workflow

### 1. Issue erstellen
Bevor Sie mit der Arbeit beginnen, erstellen Sie bitte ein Issue um:
- Das Problem/Feature zu beschreiben
- Implementation-Ansätze zu diskutieren
- Feedback von Maintainern zu erhalten

### 2. Fork & Branch
```bash
# Repository forken (über GitHub UI)
git clone https://github.com/yourusername/supercalc-security-benchmark.git
cd supercalc-security-benchmark

# Feature Branch erstellen
git checkout -b feature/neue-vulnerability-typ
# oder
git checkout -b fix/build-issue-linux
# oder  
git checkout -b docs/improve-readme
```

### 3. Entwicklung
- Folgen Sie den Code-Stil Richtlinien (siehe unten)
- Fügen Sie Tests für neue Features hinzu
- Aktualisieren Sie die Dokumentation entsprechend
- Testen Sie Ihre Änderungen gründlich

### 4. Commit Standards
Wir verwenden Conventional Commits:

```bash
# Neue Features
git commit -m "feat: add XXE vulnerability in XML parser"

# Bug Fixes  
git commit -m "fix: resolve compilation error on Ubuntu 20.04"

# Dokumentation
git commit -m "docs: update vulnerability trigger examples"

# Tests
git commit -m "test: add unit tests for memory pool allocation"

# Refactoring
git commit -m "refactor: improve error handling in admin module"
```

### 5. Pull Request
- Stellen Sie sicher, dass alle Tests durchlaufen
- Beschreiben Sie Ihre Änderungen ausführlich
- Verlinken Sie relevante Issues
- Warten Sie auf Code Review

## 🛠️ Development Setup

### Lokale Umgebung
```bash
# Dependencies installieren
sudo apt-get update
sudo apt-get install g++ cmake clang-format valgrind

# Development Build mit Sanitizers
g++ -std=c++20 -fsanitize=address -fsanitize=undefined -g \
    -Wall -Wextra -Wpedantic \
    -o supercalc_debug enhanced_calc.cpp -pthread

# Memory Leak Detection
valgrind --leak-check=full --show-leak-kinds=all ./supercalc_debug

# Static Analysis
clang-static-analyzer enhanced_calc.cpp
```

### Code Style Guidelines
```cpp
// 1. Konsistente Einrückung (4 Spaces)
namespace security_module {
    class VulnerabilityManager {
    public:
        void process_input(const std::string& user_data);
    private:
        bool validate_buffer_bounds(size_t length);
    };
}

// 2. Aussagekräftige Kommentare für Vulnerabilities
// VULNERABILITY #X: Description of the security issue
// Root Cause: Why this is vulnerable
// Exploitation: How an attacker could exploit this
void vulnerable_function(char* buffer, size_t size) {
    // Bug: Missing bounds check allows overflow
    strcpy(buffer, user_input.c_str()); // BUFFER OVERFLOW!
}

// 3. Konsistente Namenskonventionen
class MyClass {};           // PascalCase für Klassen
void my_function() {};      // snake_case für Funktionen  
int my_variable = 0;        // snake_case für Variablen
const int MAX_SIZE = 100;   // UPPER_CASE für Konstanten
```

## 🐛 Neue Vulnerabilities hinzufügen

### Design-Prinzipien
1. **Realistische Implementierung** - Sollte in echtem Code vorkommen können
2. **Subtile Versteckung** - Nicht offensichtlich vulnerable
3. **Schwerwiegende Auswirkung** - Führt zu echter Kompromittierung
4. **Klare Dokumentation** - Vollständig in exploits.md beschrieben

### Template für neue Vulnerabilities
```cpp
// ============================================================
// VULNERABILITY #X: [Name] ([Schweregrad])
// CVE Classification: [CWE-Number]
// Location: [namespace::function]
// ============================================================

namespace [module_name] {

// [Beschreibung der Funktionalität]
// VULNERABILITY: [Kurze Beschreibung des Problems]
class VulnerableClass {
public:
    // Bug: [Beschreibung warum das vulnerable ist]
    void vulnerable_method(const std::string& input) {
        // [Problematischer Code hier]
        // Führt zu: [Art der Kompromittierung]
    }
};

} // namespace [module_name]
```

### Dokumentation aktualisieren
Für jede neue Vulnerability:

1. **enhanced_exploits.md erweitern:**
   - Technische Details
   - Exploitation-Schritte
   - CVSS Score
   - Empfohlene Korrektur

2. **README.md aktualisieren:**
   - Vulnerability-Zähler erhöhen
   - Trigger-Beispiele hinzufügen

3. **Tests hinzufügen:**
   - Funktionalitäts-Tests
   - Exploitation-Verifikation

## 📊 Benchmark-Ergebnisse beitragen

### Datenformat
```markdown
## Benchmark Result: [Model Name]

**Model:** [z.B. Qwen 3.6-35B-Instruct]  
**Date:** 2025-01-15  
**Environment:** [OS, Hardware, etc.]  
**Prompt:** [Used prompt template]  

### Results
- **Time to find all vulnerabilities:** 4:32 minutes
- **Vulnerabilities found:** 8/9
- **False positives:** 1
- **Missed vulnerabilities:** #6 (Race Condition)

### Detailed Analysis
[Qualitative Bewertung der Antworten...]

### Raw LLM Output  
```
[Vollständige LLM Response hier...]
```
```

### Contribution via Issue
Erstellen Sie ein Issue mit Template `benchmark-result.md` und teilen Sie:
- Modell-Details und Version
- Verwendete Prompts
- Zeitstempel und Ergebnisse
- Qualitative Bewertung der Antworten

## 🧪 Testing Guidelines

### Minimum Test Requirements
Alle Contributions müssen folgende Tests bestehen:

```bash
# 1. Compilation Test  
g++ -std=c++20 -O2 -Wall -Wextra -o supercalc enhanced_calc.cpp -pthread

# 2. Basic Functionality Test
echo -e "2+3\n5*7\nquit" | ./supercalc

# 3. Vulnerability Trigger Tests
# [Spezifische Tests für jede Vulnerability]

# 4. Memory Safety (optional aber empfohlen)
valgrind --error-exitcode=1 --leak-check=full ./supercalc < test_input.txt
```

### Test Coverage
- Alle neuen Vulnerabilities müssen Trigger-Tests haben
- Bestehende Funktionalität darf nicht beeinträchtigt werden
- Cross-Platform Kompatibilität (Linux/macOS/WSL)

## 📝 Dokumentation Standards

### Code-Kommentare
```cpp
// VULNERABILITY #X: [Name] - [Severity]
// Description: [Was macht dieser Code vulnerable]
// Trigger: [Wie man die Vulnerability auslöst]
// Impact: [Was ein Angreifer erreichen kann]
void vulnerable_function() {
    // Bug: [Spezifisches Problem]
    dangerous_operation(); // SECURITY ISSUE!
}
```

### Markdown Standards
- Verwenden Sie klare Headers (##, ###)
- Code-Blöcke mit Syntax-Highlighting
- Tabellen für strukturierte Daten
- Emoji für bessere Lesbarkeit (sparsam)

## 🚫 Was wir NICHT akzeptieren

### Unerwünschte Contributions:
- ❌ Triviale/offensichtliche Vulnerabilities
- ❌ Malicious Code ohne Bildungswert
- ❌ Breaking Changes ohne Diskussion
- ❌ Schlecht dokumentierte Änderungen
- ❌ Plagiate oder Copyright-Verletzungen

### Ethische Richtlinien:
- ✅ Alle Vulnerabilities müssen für Bildungszwecke sein
- ✅ Klare Warnungen vor produktivem Einsatz
- ✅ Respektvoller Umgang in Diskussionen
- ✅ Fokus auf konstruktive Verbesserungen

## 🏆 Anerkennung

Alle Contributors werden in der README.md und Release Notes erwähnt. Bedeutende Beiträge können zu Maintainer-Status führen.

### Maintainer-Kriterien:
- Mehrere qualitativ hochwertige Contributions
- Aktive Teilnahme an Issue-Diskussionen  
- Demonstration von Security-Expertise
- Engagement für Projekt-Vision

## 📬 Kommunikation

- **GitHub Issues:** Für Bugs, Feature Requests, Diskussionen
- **GitHub Discussions:** Für allgemeine Fragen und Community-Austausch  
- **Email:** Für private/sensitive Angelegenheiten

---

Vielen Dank, dass Sie zur Verbesserung des SuperCalc Sicherheitsbenchmarks beitragen! 🙏

*Gemeinsam machen wir AI-Systeme sicherer!* 🛡️🤖
