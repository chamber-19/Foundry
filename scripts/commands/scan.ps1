Write-Host "Forcing RAG scan + issue creation..."
$repoRoot = if ($env:FOUNDRY_REPO_ROOT) { $env:FOUNDRY_REPO_ROOT } else { "$env:USERPROFILE\Documents\GitHub\Foundry" }
& "$repoRoot\scripts\automation\auto-issue-pipeline.ps1"
Write-Host "Scan complete."
