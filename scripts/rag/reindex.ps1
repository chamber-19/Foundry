# RAG Re-Index with Safe Git Pull
# Pulls latest from Office repo, then re-indexes the codebase
$logFile = "$HOME\.office-rag-db\reindex.log"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$repos = @(
    "C:\Users\koraj\OneDrive\Documents\GitHub\Office"
)

function Safe-Pull {
    param([string]$RepoPath)
    $repoName = Split-Path $RepoPath -Leaf

    if (-not (Test-Path "$RepoPath\.git")) {
        return "$repoName | SKIPPED -- not a git repo"
    }

    Push-Location $RepoPath
    try {
        # Check for local changes
        $dirty = git status --porcelain 2>&1
        $stashed = $false

        if ($dirty) {
            git stash push -m "auto-stash-before-reindex-$(Get-Date -Format 'yyyyMMdd-HHmmss')" 2>&1 | Out-Null
            $stashed = $true
        }

        # Pull latest
        $pullResult = git pull --ff-only 2>&1 | Out-String
        $pullSuccess = $LASTEXITCODE -eq 0

        # Restore stash if we stashed
        if ($stashed) {
            $popResult = git stash pop 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                # Stash pop conflicted -- drop it and log
                git checkout -- . 2>&1 | Out-Null
                git stash drop 2>&1 | Out-Null
                return "$repoName | PULLED but stash pop CONFLICTED -- local changes dropped. $pullResult"
            }
        }

        if ($pullSuccess) {
            $shortLog = ($pullResult -split "`n" | Select-Object -First 2) -join " "
            return "$repoName | OK -- $shortLog"
        } else {
            return "$repoName | PULL FAILED (ff-only) -- $pullResult"
        }
    } catch {
        return "$repoName | ERROR -- $($_.Exception.Message)"
    } finally {
        Pop-Location
    }
}

# Pull all repos
$pullResults = @()
foreach ($repo in $repos) {
    $result = Safe-Pull -RepoPath $repo
    $pullResults += $result
    "$timestamp | GIT | $result" | Out-File -Append -FilePath $logFile
}

# Re-index
try {
    $output = & python "C:\Users\koraj\OneDrive\Documents\GitHub\Office\scripts\rag\index.py" 2>&1 | Out-String
    "$timestamp | REINDEX | SUCCESS | $output" | Out-File -Append -FilePath $logFile
} catch {
    "$timestamp | REINDEX | FAILED | $($_.Exception.Message)" | Out-File -Append -FilePath $logFile
}
