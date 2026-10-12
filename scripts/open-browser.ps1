$ErrorActionPreference = 'SilentlyContinue'
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    try {
        $response = Invoke-WebRequest 'http://127.0.0.1:27123/api/environment' -UseBasicParsing -TimeoutSec 1
        if ($response.StatusCode -eq 200) { Start-Process 'http://127.0.0.1:27123'; exit }
    } catch {}
    Start-Sleep -Milliseconds 500
}
