Write-Host "Forcing PR review cycle..."
$repoRoot = if ($env:FOUNDRY_REPO_ROOT) { $env:FOUNDRY_REPO_ROOT } else { "$env:USERPROFILE\Documents\GitHub\Foundry" }
& "$repoRoot\scripts\automation\auto-pr-review.ps1"
Write-Host "Review complete."
