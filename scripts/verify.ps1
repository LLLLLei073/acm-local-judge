$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet publish src/Judge.Server/Judge.Server.csproj -c Release -o artifacts/server
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed' }
dotnet publish src/Judge.Worker/Judge.Worker.csproj -c Release -o artifacts/worker
if ($LASTEXITCODE -ne 0) { throw 'Worker build failed' }
dotnet run --project tests/Judge.Smoke/Judge.Smoke.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Native judge verification failed' }
dotnet run --project tests/Judge.MultiprocessTests/Judge.MultiprocessTests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Multiprocess relay verification failed' }
dotnet run --project tests/Judge.ApiTests/Judge.ApiTests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'API verification failed' }
dotnet run --project tests/Judge.FlowTests/Judge.FlowTests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Visual communication flow verification failed' }
Push-Location web
try {
    pnpm test
    if ($LASTEXITCODE -ne 0) { throw 'Frontend behavior verification failed' }
    pnpm build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
} finally { Pop-Location }
