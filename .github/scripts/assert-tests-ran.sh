#!/usr/bin/env bash
# Fails when a test run executed zero tests, which dotnet test otherwise reports as a pass.
set -euo pipefail
trx="${1:?usage: assert-tests-ran.sh <results.trx>}"
if [ ! -f "$trx" ]; then
  echo "::error::No test results found at $trx"
  exit 1
fi
total=$(grep -o '<Counters [^>]*total="[0-9]*"' "$trx" | grep -o 'total="[0-9]*"' | grep -o '[0-9]*' | head -n 1)
echo "Tests run: ${total:-0}"
if [ "${total:-0}" -eq 0 ]; then
  echo "::error::No tests ran. Check that the --filter matches the tests' [Trait] tags."
  exit 1
fi