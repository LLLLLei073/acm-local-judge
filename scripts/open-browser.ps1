param([int]$Port = 27123)
$ErrorActionPreference = 'SilentlyContinue'
$serviceUrl = "http://127.0.0.1:$Port"
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        $response = Invoke-RestMethod "$serviceUrl/healthz" -TimeoutSec 1
        if ($response.app -eq 'acm-local-judge' -and $response.status -eq 'ok') { Start-Process $serviceUrl; exit }
    } catch {}
    Start-Sleep -Milliseconds 500
}
