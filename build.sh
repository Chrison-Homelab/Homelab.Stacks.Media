#!/usr/bin/env bash
# Media stack pipeline entrypoint (Fallout build). Requires the .NET 10 SDK on
# PATH (see global.json) and, for the validator download, a `gh` with read access to
# the private Chrison-Homelab/Homelab repo.
#
#   ./build.sh                                  # default target: ValidateShapes
#   ./build.sh Bundle                           # validate + produce dist/
#   ./build.sh Release --dry-run                # everything except cutting the release
#   ./build.sh Bundle --skip ValidateShapes     # on macOS/Windows (validator is linux-x64)
set -eo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$SCRIPT_DIR/build/_build.csproj" -- "$@"
