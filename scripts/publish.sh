#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="${1:-$repo_root/artifacts/diligence}"

dotnet publish "$repo_root/src/Diligence.Cli/Diligence.Cli.fsproj" \
  --configuration Release \
  --self-contained false \
  --output "$output_dir"

printf 'Published diligence to %s\n' "$output_dir"
