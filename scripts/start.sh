#!/usr/bin/env bash
# Build and run the judge server on Linux. Usage: scripts/start.sh [--skip-build]
set -euo pipefail
cd "$(dirname "$0")/.."
for tool in dotnet node pnpm; do
  command -v "$tool" >/dev/null 2>&1 || { echo "Missing dependency: $tool. See docs/deploy.md."; exit 1; }
done
if [[ "${1:-}" != "--skip-build" ]]; then
  dotnet publish src/Judge.Worker/Judge.Worker.csproj -c Release -o artifacts/worker
  dotnet publish src/Judge.Server/Judge.Server.csproj -c Release -o artifacts/server
  (cd web && pnpm install --frozen-lockfile && pnpm build)
fi
export ACM_ROOT="${ACM_ROOT:-$PWD}"
echo "ACM Local Judge listening on ${ACM_HOST:-127.0.0.1}:${ACM_PORT:-27123}. Keep this terminal running; Ctrl+C to stop."
exec dotnet artifacts/server/Judge.Server.dll
