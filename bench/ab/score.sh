#!/usr/bin/env bash
# Usage: bench/ab/score.sh <arm-dir>
# Builds each TaskNN.fs on its own against TorchSharp 0.107.0 with plain dotnet build,
# independent of Soothsayer. Writes <arm-dir>/score.tsv: task, status, error count, first error.
set -u
here="$(cd "$(dirname "$0")" && pwd)"
arm="$(cd "$1" && pwd)"
scorer="$here/scorer"
out="$arm/score.tsv"
printf 'task\tstatus\terrors\tfirst_error\n' > "$out"
for n in $(seq -w 1 20); do
  f="$arm/Task$n.fs"
  if [ ! -f "$f" ]; then printf 'Task%s\tmissing\t-\t-\n' "$n" >> "$out"; continue; fi
  cp "$f" "$scorer/Task.fs"
  log="$arm/Task$n.build.log"
  dotnet build "$scorer/Scorer.fsproj" --no-restore -nologo -clp:NoSummary > "$log" 2>&1
  errs=$(grep -E ': error ' "$log" | sort -u)
  cnt=$(printf '%s' "$errs" | grep -c ': error ' || true)
  if [ "$cnt" -eq 0 ] && grep -q 'Build succeeded' "$log"; then st=ok; else st=fail; fi
  first=$(printf '%s' "$errs" | head -1 | sed -E 's/.*: error /error /; s/ \[.*//' | tr '\t' ' ')
  printf 'Task%s\t%s\t%s\t%s\n' "$n" "$st" "$cnt" "$first" >> "$out"
done
rm -f "$scorer/Task.fs"
cat "$out"
