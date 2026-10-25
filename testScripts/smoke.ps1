param(
    [string]$BaseUrl = "http://localhost:3000"
)
# Non-AI smoke test: URL API + governance endpoints. Makes no OpenAI calls.
$ErrorActionPreference = "Stop"

function Assert-True($condition, $message) {
    if (-not $condition) { throw "FAILED: $message" }
    Write-Host "ok  $message"
}

$health = Invoke-RestMethod "$BaseUrl/health"
Assert-True ($health.status -eq "ok") "health endpoint"

$alias = "smoke" + (Get-Random -Maximum 99999)
$created = Invoke-RestMethod -Method Post "$BaseUrl/api/urls" -ContentType "application/json" `
    -Body (@{ url = "https://example.com/smoke"; alias = $alias } | ConvertTo-Json)
Assert-True ($created.code -eq $alias) "create short URL"

try { Invoke-RestMethod -Method Post "$BaseUrl/api/urls" -ContentType "application/json" `
        -Body (@{ url = "javascript:alert(1)" } | ConvertTo-Json) | Out-Null; $rejected = $false }
catch { $rejected = $_.Exception.Response.StatusCode.value__ -eq 400 }
Assert-True $rejected "unsafe destination rejected"

$redirect = curl.exe -s -o NUL -w "%{http_code}" "$BaseUrl/$alias"
Assert-True ($redirect -eq "302") "redirect resolves"
$details = Invoke-RestMethod "$BaseUrl/api/urls/$alias"
Assert-True ($details.clicks -eq 1) "click recorded"

$scenarios = (Invoke-RestMethod "$BaseUrl/api/scenarios").scenarios
Assert-True ($scenarios.Count -eq 3) "three scenarios exposed"
Assert-True (($scenarios[0].dependencyGraph | Where-Object approvalRequired).Count -ge 2) "graph has human approval gates"

$metrics = Invoke-RestMethod "$BaseUrl/api/metrics"
Assert-True ($null -ne $metrics.totalRuns) "metrics endpoint"
Write-Host "`nSmoke test passed."
