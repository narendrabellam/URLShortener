param(
    [string]$BaseUrl = "http://localhost:3000",
    [ValidateSet("greenfield", "brownfield", "ambiguous")][string]$Scenario = "greenfield",
    [switch]$AutoApprove,
    [switch]$SaveEvidence
)
# Real AI demo. Requires OPENAI_API_KEY on the API process (billable). The run pauses for human approval.
$ErrorActionPreference = "Stop"

$health = Invoke-RestMethod "$BaseUrl/health"
if (-not $health.aiConfigured) { throw "The API has no OPENAI_API_KEY configured. Set it in .env or your environment and restart." }

function Wait-Run($id, [scriptblock]$done) {
    for ($i = 0; $i -lt 600; $i++) {
        $run = Invoke-RestMethod "$BaseUrl/api/runs/$id"
        if (& $done $run) { return $run }
        Start-Sleep -Seconds 2
    }
    throw "Timed out waiting for run $id."
}

$run = Invoke-RestMethod -Method Post "$BaseUrl/api/runs" -ContentType "application/json" -Body (@{ scenarioId = $Scenario } | ConvertTo-Json)
Write-Host "Started run $($run.id) ($Scenario)"
$run = Wait-Run $run.id { param($r) $r.status -ne "running" }
if ($run.status -ne "waiting_approval") { Write-Host "Run ended as $($run.status)"; $run.nodes.PSObject.Properties | ForEach-Object { "{0}: {1} {2}" -f $_.Name, $_.Value.status, $_.Value.error }; exit 1 }

$approval = $run.nodes.'change-approval'.output
Write-Host "`nProposed change set (digest $($approval.changeSetDigest)):"
$approval.files | ForEach-Object { "  $($_.path)  ($($_.characters) chars)" }
if (-not $AutoApprove) { if ((Read-Host "Approve this change set for sandbox validation? (y/N)") -ne "y") { Invoke-RestMethod -Method Post "$BaseUrl/api/runs/$($run.id)/approval" -ContentType "application/json" -Body (@{ approved = $false; reason = "Rejected in demo." } | ConvertTo-Json) | Out-Null; Write-Host "Rejected; run safe-stopped."; exit 0 } }

Invoke-RestMethod -Method Post "$BaseUrl/api/runs/$($run.id)/approval" -ContentType "application/json" `
    -Body (@{ approved = $true; reason = "Reviewed in demo."; changeSetDigest = $approval.changeSetDigest } | ConvertTo-Json) | Out-Null
Write-Host "Validating in sandbox (dotnet build + dotnet test)..."
$run = Wait-Run $run.id { param($r) $r.nodes.'release-approval'.status -eq "waiting_approval" -or $r.status -eq "safe_stopped" }
if ($run.status -eq "safe_stopped") { Write-Host "Safe-stopped: validation or a gate did not pass (change discarded)."; exit 1 }

Write-Host "Validation passed. Approving release readiness (no deployment occurs)."
Invoke-RestMethod -Method Post "$BaseUrl/api/runs/$($run.id)/approval" -ContentType "application/json" -Body (@{ approved = $true; reason = "Ready." } | ConvertTo-Json) | Out-Null
$run = Wait-Run $run.id { param($r) $r.status -in @("succeeded", "safe_stopped") }

$evidence = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/evidence"
$audit = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/audit/verify"
Write-Host "`nStatus: $($run.status)   agent calls: $($run.metrics.agentCalls)   tokens: $($run.metrics.inputTokens)/$($run.metrics.outputTokens)"
$evidence.validations | Where-Object { $_.nodeId -eq "validation" } | ForEach-Object { "  validation {0}: {1}" -f $_.check, $_.passed }
Write-Host "Audit chain valid: $($audit.valid) ($($audit.events) events)"

if ($SaveEvidence) {
    # Compact, secret-free record of this real run (statuses, summary, validation checks, metrics, audit verification).
    $summary = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)/summary"
    $record = [ordered]@{
        scenario = $Scenario; runId = $run.id; status = $run.status; planVersion = $run.requirementVersion
        capturedAt = (Get-Date).ToUniversalTime().ToString("o")
        nodes = @($run.nodes.PSObject.Properties | ForEach-Object { [ordered]@{ node = $_.Name; stage = $_.Value.stage; status = $_.Value.status; attempts = $_.Value.attempts } })
        metrics = $run.metrics
        validations = @($evidence.validations | ForEach-Object { [ordered]@{ nodeId = $_.nodeId; check = $_.check; passed = $_.passed } })
        auditChain = [ordered]@{ valid = $audit.valid; events = $audit.events }
        summary = $summary
    }
    $folder = Join-Path $PSScriptRoot "..\docs\evidence"
    New-Item -ItemType Directory -Force $folder | Out-Null
    $path = Join-Path $folder "$Scenario.json"
    $json = $record | ConvertTo-Json -Depth 10
    # Remove machine-specific sandbox and user paths from captured build/test output.
    $json = [regex]::Replace($json, '[A-Za-z]:\\\\Users\\\\[^\\"]+\\\\AppData\\\\Local\\\\Temp\\\\linkforge-sandbox-[0-9a-f]+', '<sandbox>')
    $json | Set-Content -Path $path -Encoding utf8
    Write-Host "Evidence saved to $path"
}
