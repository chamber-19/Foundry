param([string]$prRef)

$stateRoot = if ($env:FOUNDRY_STATE_ROOT) { $env:FOUNDRY_STATE_ROOT } else { "$HOME\FoundryState" }
if (-not (Test-Path $stateRoot)) { New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null }

if (-not $prRef) {
    Write-Host "Usage: approve Office#27  or  approve Suite#54"
    return
}

$memoryFile = "$stateRoot\decision-memory.json"
$ghToken = $env:GITHUB_TOKEN
$headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

# Parse repo and PR number
if ($prRef -match '^(Foundry|Suite)#(\d+)$') {
    $repoShort = $Matches[1]
    $prNumber = $Matches[2]
    $repo = "Koraji95-coder/$repoShort"
} else {
    Write-Host "Invalid format. Use: approve Foundry#27"
    return
}

# Get PR details
try {
    $pr = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/pulls/$prNumber" -Headers $headers
} catch {
    Write-Host "Could not find PR #$prNumber in $repo"
    return
}

# Log the decision
$memory = Get-Content $memoryFile | ConvertFrom-Json
$entry = @{
    decision   = "approved"
    repo       = $repoShort
    pr_number  = [int]$prNumber
    title      = $pr.title
    labels     = @($pr.labels | ForEach-Object { $_.name })
    files_changed = $pr.changed_files
    timestamp  = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
}

$memory = @($memory) + $entry
$memory | ConvertTo-Json -Depth 4 | Set-Content -Path $memoryFile -Encoding UTF8

Write-Host "Logged APPROVED: $($pr.title)"
