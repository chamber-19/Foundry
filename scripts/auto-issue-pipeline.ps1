# Ensure Ollama has a model loaded
$ollamaRunning = $false
try {
    $ps = Invoke-RestMethod -Uri "http://localhost:11434/api/ps" -ErrorAction Stop
    if ($ps.models.Count -gt 0) { $ollamaRunning = $true }
} catch {}

if (-not $ollamaRunning) {
    Write-Host "Loading qwen3:14b..."
    Start-Process -FilePath "ollama" -ArgumentList "run qwen3:14b" -WindowStyle Hidden
    Start-Sleep -Seconds 30
}

$webhook = "https://discord.com/api/webhooks/1490590808603361291/SCVngVWu8BmQ87KBfwWZsKjk1nlwrOmSMcfy8F_tn2v2ELtJcDLGWKNhO3Zwy5pAMl_l"
$userId = "1356296581472718988"
$ghToken = $env:GITHUB_TOKEN
$headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

# === CONFIGURABLE: How many issues per cycle ===
$issuesPerCycle = 6

# Suite excluded while building ML training base
# Fetch existing issues to avoid duplicates
try {
    $officeIssues = (Invoke-RestMethod -Uri "https://api.github.com/repos/Koraji95-coder/Office/issues?state=open&per_page=20&labels=ai-suggested" -Headers $headers) | ForEach-Object { "- #$($_.number): $($_.title)" }

    $allOfficeIssues = (Invoke-RestMethod -Uri "https://api.github.com/repos/Koraji95-coder/Office/issues?state=open&per_page=30" -Headers $headers) | ForEach-Object { "- #$($_.number): $($_.title)" }

    $officeAiCount = ($officeIssues | Measure-Object).Count

    if ($officeAiCount -ge 20) {
        Write-Host "Office repo at issue cap. Skipping."
        exit 0
    }

    $allowOffice = if ($officeAiCount -lt 20) { "You MAY suggest Office issues. ($officeAiCount/20 open)" } else { "Do NOT suggest Office issues -- at cap ($officeAiCount/20)." }
} catch {
    Write-Host "Failed to fetch existing issues: $($_.Exception.Message)"
    exit 1
}

# Load decision memory
$memoryFile = "$HOME\.office-rag-db\decision-memory.json"
$memoryContext = ""
if (Test-Path $memoryFile) {
    try {
        $memories = Get-Content $memoryFile | ConvertFrom-Json | Select-Object -Last 10
        $memoryContext = "RECENT AI DECISIONS:`n" + (($memories | ForEach-Object { "- $($_.decision): $($_.repo) $($_.title)" }) -join "`n")
    } catch {}
}

# Track what we create this cycle to avoid self-duplication
$createdThisCycle = @()

# RAG query pool
$ragQueries = @(
    "What code has no error handling or missing try catch?"
    "What files have no test coverage?"
    "What API endpoints are missing input validation?"
    "What areas need better documentation?"
    "What components have hardcoded values that should be configurable?"
    "What functions are too long and need refactoring?"
    "What files are missing TypeScript types or have any types?"
    "What pages are missing accessibility attributes?"
)

for ($i = 1; $i -le $issuesPerCycle; $i++) {
    Write-Host "`n--- Issue $i of $issuesPerCycle ---"

    # Pick a different RAG query each iteration
    $ragQuery = $ragQueries[($i - 1) % $ragQueries.Count]

    $ragContext = ""
    try {
        $ragContext = python "C:\Users\koraj\OneDrive\Documents\GitHub\Office\scripts\rag\query.py" $ragQuery 2>$null | Out-String
        if ($ragContext.Length -gt 3000) {
            $ragContext = $ragContext.Substring(0, 3000)
        }
    } catch {
        $ragContext = "(RAG unavailable this cycle)"
    }

    # Build list of what we already created this cycle
    $cycleContext = ""
    if ($createdThisCycle.Count -gt 0) {
        $cycleContext = "`nALREADY CREATED THIS CYCLE (do not duplicate):`n" + ($createdThisCycle -join "`n")
    }

    $prompt = @"
You are an autonomous technical project manager. Your job is to find exactly 1 high-value issue to create right now.

Rules:
- Suggest exactly 1 issue. No more.
- $allowOffice
- Do NOT duplicate any existing issue below.
- Focus on: test coverage gaps, missing docs, technical debt, CI/CD hardening, or developer experience improvements.
- The issue must be specific and actionable -- not vague.
- Use the CODE CONTEXT below to make your suggestion based on REAL code you can see.
- Each issue should target a DIFFERENT area of the codebase.

EXISTING OPEN ISSUES (do not duplicate):

OFFICE:
$($allOfficeIssues -join "`n")
$cycleContext

CODE CONTEXT FROM THE CODEBASE (use this to find real problems):
$memoryContext

$ragContext

Respond ONLY with valid JSON. No markdown, no explanation:
{ "repo": "Office", "title": "issue title", "body": "2-3 sentence description referencing specific files" }
"@

    $chatBody = @{
        model    = "qwen3:14b"
        messages = @(@{ role = "user"; content = $prompt })
        stream   = $false
    } | ConvertTo-Json -Depth 3

    try {
        $response = Invoke-RestMethod -Uri "http://localhost:11434/api/chat" -Method POST -ContentType "application/json" -Body $chatBody
        $content = $response.message.content

        # Strip markdown fences if present
        $content = $content -replace '(?s)```json\s*', '' -replace '(?s)```\s*', ''
        $content = $content.Trim()

        $issue = $content | ConvertFrom-Json

        if (-not $issue.repo -or -not $issue.title -or -not $issue.body) {
            Write-Host "Invalid response from Ollama, skipping."
            continue
        }

        # Safety net: Suite excluded while building ML training base
        if ($issue.repo -ne "Office") {
            Write-Host "Skipping non-Office suggestion ($($issue.repo)). Suite excluded while building ML training base."
            continue
        }

        $repo = "Koraji95-coder/$($issue.repo)"
        $issueBody = @{
            title  = $issue.title
            body   = "$($issue.body)`n`n---`n*Auto-generated by AI Pipeline (qwen3:14b + RAG)*"
            labels = @("ai-suggested")
        } | ConvertTo-Json -Depth 3

        $result = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/issues" `
            -Method POST -Headers $headers -ContentType "application/json" -Body $issueBody

        Write-Host "Created: $repo#$($result.number) -- $($issue.title)"

        # Assign to Copilot
        $assignBody = '{"assignees":["copilot-swe-agent[bot]"]}'
        try {
            Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/issues/$($result.number)" `
                -Method PATCH -Headers $headers -ContentType "application/json" -Body $assignBody
        } catch {}

        # Track for dedup within this cycle
        $createdThisCycle += "- $($issue.repo): $($issue.title)"

        # Discord notification
        $payload = @{
            content = "<@$userId>"
            embeds = @(@{
                title       = "New Issue: $($issue.title)"
                description = "$($issue.body)`n`n[View Issue]($($result.html_url))"
                color       = 3447003
                footer      = @{ text = "$($issue.repo) | #$($result.number) | AI Pipeline" }
                timestamp   = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
            })
        } | ConvertTo-Json -Depth 5 -Compress
        Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json; charset=utf-8" -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))

        Start-Sleep -Seconds 3
    } catch {
        Write-Host "Error on issue $i : $($_.Exception.Message)"
    }
}

Write-Host "`nPipeline complete. Created $($createdThisCycle.Count) issues."


