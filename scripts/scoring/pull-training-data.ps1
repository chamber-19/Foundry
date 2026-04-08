# ============================================================
# PULL-TRAINING-DATA.ps1
# Runs on DUSTIN (100.93.111.63)
#
# Pulls historical-scores.jsonl from Machine 2 (100.65.90.57)
# via the Python HTTP file server running on port 8787, saves
# it to $HOME\.office-rag-db\historical-scores.jsonl, and
# reports how many new records were pulled.
#
# Can be run manually or as a scheduled task.
# ============================================================

# HTTP is intentional — traffic travels over the encrypted Tailscale VPN tunnel
$machine2Url = "http://100.65.90.57:8787/historical-scores.jsonl"
$outputDir   = "$HOME\.office-rag-db"
$outputFile  = "$outputDir\historical-scores.jsonl"
$tmpFile     = "$outputDir\historical-scores.jsonl.tmp"

# Ensure output directory exists
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Count existing records before pulling
$existingCount = 0
if (Test-Path $outputFile) {
    $existingCount = (Get-Content $outputFile | Where-Object { $_.Trim() -ne "" }).Count
}

Write-Host "Pulling historical scores from Machine 2..."
Write-Host "  Source : $machine2Url"
Write-Host "  Dest   : $outputFile"

try {
    Invoke-WebRequest -Uri $machine2Url -OutFile $tmpFile -UseBasicParsing -ErrorAction Stop
} catch {
    Write-Error "Failed to pull from Machine 2: $($_.Exception.Message)"
    if (Test-Path $tmpFile) { Remove-Item $tmpFile -Force }
    exit 1
}

# Count records in pulled file
$pulledCount = (Get-Content $tmpFile | Where-Object { $_.Trim() -ne "" }).Count

# Overwrite with latest data from Machine 2
Move-Item -Path $tmpFile -Destination $outputFile -Force

$newRecords = $pulledCount - $existingCount
if ($newRecords -lt 0) { $newRecords = 0 }

Write-Host "`n=== Pull complete ==="
Write-Host "  Records before : $existingCount"
Write-Host "  Records after  : $pulledCount"
Write-Host "  New records    : $newRecords"
