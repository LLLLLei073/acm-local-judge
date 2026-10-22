param([string]$Url, [string]$Compiler = 'D:\mingw64\bin\g++.exe', [string]$SecretsFile = 'artifacts/cloud-deploy/secrets.json')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (!$Url) { throw '请用 -Url 指定云端站点地址' }
if (!(Test-Path -LiteralPath $Compiler)) { throw '本机 C++ 编译器不存在' }
$agentSecrets = Get-Content -LiteralPath $SecretsFile -Raw | ConvertFrom-Json
$env:ACM_CLOUD_URL = $Url
$env:ACM_WORKER_TOKEN = $agentSecrets.workerToken
$env:ACM_COMPILER = $Compiler
dotnet publish src/Judge.Agent/Judge.Agent.csproj -c Release -o artifacts/agent
if ($LASTEXITCODE -ne 0) { throw '评测端构建失败' }
dotnet artifacts/agent/Judge.Agent.dll
