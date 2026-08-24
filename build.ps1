#!/usr/bin/env pwsh
# Media stack pipeline entrypoint (Fallout build). Requires the .NET 10 SDK on
# PATH (see global.json) and, for the validator download, a `gh` with read access to
# the private Chrison-Homelab/Homelab repo.
#
#   ./build.ps1                                 # default target: ValidateShapes
#   ./build.ps1 Bundle                          # validate + produce dist/
#   ./build.ps1 Release --dry-run               # everything except cutting the release
#   ./build.ps1 Bundle --skip ValidateShapes    # on macOS/Windows (validator is linux-x64)
$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet run --project "$ScriptDir/build/_build.csproj" -- @args
exit $LASTEXITCODE
