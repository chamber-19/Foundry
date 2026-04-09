param([string]$prRef, [string]$reason = "No reason given")

$stateRoot = if ($env:FOUNDRY_STATE_ROOT) { $env:FOUNDRY_STATE_ROOT } else { "$HOME\FoundryState" }
if (-not (Test-Path $stateRoot)) { New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null }

if (-not $prRef) {
    Write-Host "Usage: reject Office#31 'too vague'  or  reject Suite#57"
    return
}

$memoryFile = "$stateRoot\decision-memory.json"
$ghToken = $env:GITHUB_TOKEN
$headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

if ($prRef -match '^(Office|Suite)#(\d+)$') {
    $repoShort = $Matches[1]
    $prNumber = $Matches[2]
    $repo = "Koraji95-coder/$repoShort"
} else {
    Write-Host "Invalid format. Use: reject Office#31 'reason here'"
    return
}

try {
    $pr = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/pulls/$prNumber" -Headers $headers
} catch {
    Write-Host "Could not find PR #$prNumber in $repo"
    return
}

$memory = Get-Content $memoryFile | ConvertFrom-Json
$entry = @{
    decision   = "rejected"
    repo       = $repoShort
    pr_number  = [int]$prNumber
    title      = $pr.title
    reason     = $reason
    labels     = @($pr.labels | ForEach-Object { $_.name })
    files_changed = $pr.changed_files
    timestamp  = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
}

$memory = @($memory) + $entry
$memory | ConvertTo-Json -Depth 4 | Set-Content -Path $memoryFile -Encoding UTF8

Write-Host "Logged REJECTED: $($pr.title) -- Reason: $reason"
