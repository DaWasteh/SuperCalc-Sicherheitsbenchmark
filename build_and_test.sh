#!/bin/bash

# SuperCalc Sicherheitsbenchmark - Build and Test Script
# ======================================================

echo "SuperCalc Sicherheitsbenchmark"
echo "==============================="
echo ""

# Check for C++ compiler
if ! command -v g++ &> /dev/null; then
    echo "Error: g++ compiler not found. Please install g++ first."
    exit 1
fi

echo "Building SuperCalc Professional..."

# Compile with proper flags
g++ -std=c++20 -O2 -o supercalc enhanced_calc.cpp -pthread

if [ $? -eq 0 ]; then
    echo "✓ Build successful!"
    echo ""
else
    echo "✗ Build failed!"
    exit 1
fi

# Basic functionality test
echo "Running basic functionality test..."
echo ""

# Test basic arithmetic
echo "Testing basic calculations:"
{
    echo "2+3"
    sleep 0.5
    echo "10/2"
    sleep 0.5
    echo "5*7"
    sleep 0.5
    echo "fact(5)"
    sleep 0.5
    echo "var set x 10"
    sleep 0.5
    echo "var get x"
    sleep 0.5
    echo "quit"
} | timeout 10s ./supercalc

echo ""
echo "Build and test complete!"
echo ""
echo "Usage:"
echo "  ./supercalc                    # Start calculator"
echo "  cat enhanced_exploits.md      # View vulnerability documentation"
echo "  cat README.md                 # Full documentation"
echo ""
echo "Example vulnerability triggers:"
echo "  Input: %x%x%x%x               # Format string bug"  
echo "  Input: fact(25)               # Integer overflow"
echo "  Admin: EMERGENCY_OVERRIDE     # Logic bomb"
echo ""
echo "⚠️  WARNING: This is a security benchmark with real vulnerabilities!"
echo "    Use only in isolated test environments!"
echo ""
echo "GitHub: https://github.com/DaWasteh/supercalc-security-benchmark"
echo "Issues: https://github.com/DaWasteh/supercalc-security-benchmark/issues"
