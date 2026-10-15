#!/usr/bin/env bash
# Full acceptance suite for Linux servers. Mirrors scripts/verify.ps1.
# Stop any running judge first: it locks the published files.
set -euo pipefail
cd "$(dirname "$0")/.."

command -v dotnet >/dev/null 2>&1 || { echo "Missing dependency: dotnet. See docs/deploy.md."; exit 1; }
command -v g++ >/dev/null 2>&1 || { echo "Missing dependency: g++. See docs/deploy.md."; exit 1; }

dotnet publish src/Judge.Server/Judge.Server.csproj -c Release -o artifacts/server
dotnet publish src/Judge.Worker/Judge.Worker.csproj -c Release -o artifacts/worker

dotnet run --project tests/Judge.Smoke/Judge.Smoke.csproj -c Release
dotnet run --project tests/Judge.MultiprocessTests/Judge.MultiprocessTests.csproj -c Release
dotnet run --project tests/Judge.ApiTests/Judge.ApiTests.csproj -c Release
dotnet run --project tests/Judge.FlowTests/Judge.FlowTests.csproj -c Release

if command -v pnpm >/dev/null 2>&1; then
  (cd web && pnpm build)
else
  echo "NOTE: pnpm not found; skipping frontend build (deploy a prebuilt web/dist)."
fi

echo "ALL LINUX CHECKS PASSED"
