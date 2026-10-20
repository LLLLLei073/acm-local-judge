param([switch]$NoBrowser, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$servicePort = if ($env:ACM_PORT) { [int]$env:ACM_PORT } else { 27123 }
$serviceUrl = "http://127.0.0.1:$servicePort"
try {
    $existing = Invoke-RestMethod "$serviceUrl/healthz" -TimeoutSec 1
    if ($existing.app -eq 'acm-local-judge') {
        if (-not $NoBrowser) { Start-Process $serviceUrl }
        Write-Host "ACM Local Judge is already running: $serviceUrl"
        exit 0
    }
} catch {}
foreach ($tool in @('dotnet', 'node', 'pnpm')) { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Missing dependency: $tool. See README.md." } }
if (-not $SkipBuild) {
    dotnet publish src/Judge.Worker/Judge.Worker.csproj -c Release -o artifacts/worker
    if ($LASTEXITCODE -ne 0) { throw 'Worker build failed' }
    dotnet publish src/Judge.Server/Judge.Server.csproj -c Release -o artifacts/server
    if ($LASTEXITCODE -ne 0) { throw 'Server build failed' }
    Push-Location web
    try {
        pnpm install --frozen-lockfile
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency install failed' }
        pnpm build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
    } finally { Pop-Location }
}
$env:ACM_ROOT = $projectRoot
if (-not $NoBrowser) {
    # The detached helper waits for readiness, without opening a PowerShell window.
    $readyScript = Join-Path $PSScriptRoot 'open-browser.ps1'
    Start-Process powershell -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $readyScript + '"'),'-Port',$servicePort)
}
Write-Host "ACM Local Judge: $serviceUrl"
Write-Host 'Keep this terminal running. Press Ctrl+C to stop.'
dotnet artifacts/server/Judge.Server.dll
