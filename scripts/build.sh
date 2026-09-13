#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

dotnet restore Diligence.sln
dotnet build Diligence.sln --no-restore
dotnet test Diligence.sln --no-build
