# ============================================================
# REPLAY-HISTORICAL.ps1
# Runs on Machine 2 (100.65.90.57)
#
# Fetches all closed PRs from Koraji95-coder/Foundry via the
# GitHub API (read-only), scores each one locally with
# qwen3:8b via Ollama, and appends results to:
#   $HOME\.office-rag-db\historical-scores.jsonl
#
# Idempotent — already-scored PRs are skipped.
# ============================================================

$repo      = "Koraji95-coder/Foundry"
$repoShort = "Foundry"
$model     = "qwen3:8b"
$outputDir = "$HOME\.office-rag-db"
$outputFile = "$outputDir\historical-scores.jsonl"
$ghToken   = $env:GITHUB_TOKEN

if (-not $ghToken) {
    Write-Error "GITHUB_TOKEN environment variable is not set."
    exit 1
}

$headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

# Ensure output directory exists
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Load already-scored PR numbers for idempotency
$alreadyScored = @{}
if (Test-Path $outputFile) {
    Get-Content $outputFile | ForEach-Object {
        try {
            $rec = $_ | ConvertFrom-Json
            $alreadyScored[$rec.pr_number] = $true
        } catch {}
    }
    Write-Host "Loaded $($alreadyScored.Count) already-scored PR(s) from $outputFile"
}

# ---- Fetch all closed PRs (paginate) ----
$allPrs = @()
$page   = 1
do {
    try {
        $batch = Invoke-RestMethod `
            -Uri "https://api.github.com/repos/$repo/pulls?state=closed&per_page=100&page=$page" `
            -Headers $headers
        $allPrs += $batch
        Write-Host "Fetched page $page — $($batch.Count) PRs (total so far: $($allPrs.Count))"
        $page++
    } catch {
        Write-Host "ERROR fetching page $page : $($_.Exception.Message)"
        break
    }
} while ($batch.Count -eq 100)

Write-Host "`nTotal closed PRs fetched: $($allPrs.Count)"

$scored = 0
$skipped = 0

foreach ($pr in $allPrs) {
    $prNum = [int]$pr.number

    # Skip if already scored
    if ($alreadyScored.ContainsKey($prNum)) {
        $skipped++
        continue
    }

    # Only score Copilot-authored PRs (same filter as live pipeline)
    if ($pr.user.login -ne "copilot-swe-agent[bot]" -and $pr.user.login -ne "Copilot") {
        $skipped++
        continue
    }

    Write-Host "`n--- Scoring $repoShort#$prNum : $($pr.title) ---"

    # Fetch full PR details + diff
    try {
        $freshPr = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/pulls/$prNum" -Headers $headers
    } catch {
        Write-Host "  SKIP: could not fetch PR details — $($_.Exception.Message)"
        $skipped++
        continue
    }

    $diffHeaders = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3.diff" }
    $diff = ""
    try {
        $diff = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/pulls/$prNum" -Headers $diffHeaders
    } catch {
        Write-Host "  WARN: could not fetch diff — $($_.Exception.Message)"
    }

    if ($diff.Length -gt 14000) {
        $diff = $diff.Substring(0, 14000) + "`n... (truncated)"
    }

    # Fetch changed files
    $fileList = @()
    try {
        $files = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/pulls/$prNum/files?per_page=100" -Headers $headers
        $fileList = @($files | ForEach-Object { $_.filename })
    } catch {
        Write-Host "  WARN: could not fetch file list — $($_.Exception.Message)"
    }

    # Build scoring prompt (same format as auto-pr-review.ps1)
    $reviewPrompt = @"
You are a strict senior code reviewer. You must be critical and honest. Do NOT rubber-stamp.

PR Title: $($pr.title)
PR Description: $($pr.body)
Changed files: $($freshPr.changed_files) | Additions: $($freshPr.additions) | Deletions: $($freshPr.deletions)

DIFF:
$diff

SCORING RULES -- follow these strictly:
- 9-10: Exceptional. Clean code, good tests, no issues, adds real value. Rare.
- 7-8: Good. Minor issues but solid contribution. Most decent PRs land here.
- 5-6: Mediocre. Missing tests, incomplete, or questionable approach.
- 3-4: Poor. Breaks things, duplicates existing code, or wrong approach entirely.
- 1-2: Reject. Empty, broken, or harmful.

You MUST respond with ONLY this JSON structure — no markdown, no extra text:
{
  "verdict": "APPROVE | REQUEST_CHANGES | NEEDS_DISCUSSION",
  "score": <integer 1-10>,
  "summary": "<2-3 sentences on what this PR does>",
  "concerns": ["<concern 1>", "<concern 2>"],
  "overlap_risk": "none | <description of overlap>"
}
"@

    $chatBody = @{
        model    = $model
        messages = @(@{ role = "user"; content = $reviewPrompt })
        stream   = $false
    } | ConvertTo-Json -Depth 3

    try {
        $response = Invoke-RestMethod -Uri "http://localhost:11434/api/chat" -Method POST -ContentType "application/json" -Body $chatBody
        $review = $response.message.content
    } catch {
        Write-Host "  SKIP: Ollama call failed — $($_.Exception.Message)"
        $skipped++
        Start-Sleep -Seconds 2
        continue
    }

    # Parse LLM response
    $parsedJson  = $null
    $verdict     = "UNKNOWN"
    $score       = 0
    $summary     = ""
    $concerns    = @()

    try {
        $parsedJson = $review | ConvertFrom-Json -ErrorAction Stop
    } catch {}

    if ($parsedJson) {
        $verdict = $parsedJson.verdict
        $score   = [int]$parsedJson.score
        $summary = $parsedJson.summary
        $concerns = if ($parsedJson.concerns) { $parsedJson.concerns } else { @() }

        if     ($verdict -match "REQUEST_CHANGES") { $verdict = "REQUEST_CHANGES" }
        elseif ($verdict -match "NEEDS_DISCUSSION") { $verdict = "NEEDS_DISCUSSION" }
        elseif ($verdict -match "APPROVE") { $verdict = "APPROVE" }
        else   { $verdict = "UNKNOWN" }
    } else {
        if     ($review -match "REQUEST_CHANGES") { $verdict = "REQUEST_CHANGES" }
        elseif ($review -match "NEEDS_DISCUSSION") { $verdict = "NEEDS_DISCUSSION" }
        elseif ($review -match "APPROVE") { $verdict = "APPROVE" }

        if      ($review -match "(\d+)\s*/\s*10")                              { $score = [int]$Matches[1] }
        elseif  ($review -match "(\d+)\s+out\s+of\s+10")                       { $score = [int]$Matches[1] }
        elseif  ($review -match "Score[:\s]+(\d+)")                            { $score = [int]$Matches[1] }

        $summary = ($review -split "`n")[0..2] -join " "
    }

    if ($score -lt 0 -or $score -gt 10) { $score = 0 }
    if ($verdict -eq "APPROVE" -and $score -eq 0) { $score = 6 }

    # Build output record
    $fileCount = if ($fileList) { $fileList.Count } else { [int]$freshPr.changed_files }
    $additions = [int]$freshPr.additions
    $deletions = [int]$freshPr.deletions

    $record = @{
        pr_number  = $prNum
        repo       = $repoShort
        title      = $pr.title
        author     = $pr.user.login
        was_merged = ($pr.merged_at -ne $null)
        score      = $score
        verdict    = $verdict
        summary    = $summary
        concerns   = $concerns
        files      = $fileList
        file_count = $fileCount
        additions  = $additions
        deletions  = $deletions
        model      = $model
        scored_at  = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        source     = "historical-replay"
    } | ConvertTo-Json -Compress -Depth 5

    Add-Content -Path $outputFile -Value $record -Encoding UTF8
    $alreadyScored[$prNum] = $true
    $scored++

    Write-Host "  -> Scored $prNum : score=$score verdict=$verdict"

    # Be nice to GitHub API
    Start-Sleep -Seconds 2
}

Write-Host "`n=== Historical replay complete: $scored scored, $skipped skipped ==="
Write-Host "Output: $outputFile"
