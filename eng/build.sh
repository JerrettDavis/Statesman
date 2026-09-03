#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"

cd "$ROOT"
python3 eng/validate.py --report artifacts/static-validation.txt
dotnet restore Statesman.slnx
dotnet build Statesman.slnx --configuration "$CONFIGURATION" --no-restore
dotnet test Statesman.slnx --configuration "$CONFIGURATION" --no-build
while IFS= read -r -d '' project; do
  dotnet pack "$project" --configuration "$CONFIGURATION" --no-build \
    --output artifacts/packages
done < <(find src -name '*.csproj' -print0 | sort -z)
