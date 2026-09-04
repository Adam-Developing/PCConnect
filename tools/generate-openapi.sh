#!/usr/bin/env bash
# =============================================================================
# Generates the OpenAPI document from the running API.
#
# The contract is generated output, not prose (C-5, 04 §1). `api/api_spec.md`
# documented endpoints that no implementation provided, which is how three
# incompatible API surfaces came to exist for the same product; a generated
# document cannot drift from the handlers that produce it.
#
# Usage:  ./tools/generate-openapi.sh [output.yaml]
# =============================================================================

set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$root/docs/architecture/openapi/pcconnect-v2.yaml}"
port="${OPENAPI_PORT:-5099}"

# Intermediate files live inside the repository rather than in /tmp: on Windows
# the shell and the Python interpreter disagree about where /tmp is.
work="$root/artifacts"

cd "$root"
mkdir -p "$work"

cleanup() {
    if [ -n "${api_pid:-}" ] && kill -0 "$api_pid" 2>/dev/null; then
        kill "$api_pid" 2>/dev/null || true
        wait "$api_pid" 2>/dev/null || true
    fi
}
trap cleanup EXIT

echo "Building the API..."
dotnet build src/PCConnect.Api/PCConnect.Api.csproj -c Release --nologo -v quiet

echo "Starting it on :$port with a throwaway configuration..."

# The generated document must not depend on a database or on real keys: it
# describes the shape of the API, and the shape lives in the code.
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:$port"
export PCCONNECT_DATABASE__CONNECTIONSTRING="Host=localhost;Port=1;Database=openapi;Username=none"
export PCCONNECT_DATABASE__MIGRATEONSTARTUP=false
export PCCONNECT_CACHE__CONNECTIONSTRING=""

dotnet run --project src/PCConnect.Api -c Release --no-build --no-launch-profile > "$work/openapi-api.log" 2>&1 &
api_pid=$!

for _ in $(seq 1 60); do
    if curl -fsS "http://localhost:$port/openapi/v1.json" -o "$work/openapi.json" 2>/dev/null; then
        break
    fi
    sleep 1
done

if [ ! -s "$work/openapi.json" ]; then
    echo "The API did not serve an OpenAPI document. Log:" >&2
    tail -30 "$work/openapi-api.log" >&2
    exit 1
fi

mkdir -p "$(dirname "$output")"

python_bin=""
for candidate in python3 python py; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import yaml' 2>/dev/null; then
        python_bin="$candidate"
        break
    fi
done

# JSON is what ASP.NET Core emits; the committed artefact is YAML because it is
# reviewable as a diff.
if [ -n "$python_bin" ]; then
    OPENAPI_INPUT="$work/openapi.json" OPENAPI_OUTPUT="$output" "$python_bin" - <<'PYTHON'
import json
import os

import yaml

with open(os.environ["OPENAPI_INPUT"], encoding="utf-8") as handle:
    document = json.load(handle)

with open(os.environ["OPENAPI_OUTPUT"], "w", encoding="utf-8", newline="\n") as handle:
    yaml.safe_dump(document, handle, sort_keys=False, allow_unicode=True, width=100)
PYTHON
elif command -v yq >/dev/null 2>&1; then
    yq -P eval '.' "$work/openapi.json" > "$output"
else
    echo "Neither PyYAML nor yq is available; writing JSON instead." >&2
    output="${output%.yaml}.json"
    cp "$work/openapi.json" "$output"
fi

echo "Wrote $output ($(wc -l < "$output") lines)"
