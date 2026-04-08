$webhook = "https://discord.com/api/webhooks/1490590808603361291/SCVngVWu8BmQ87KBfwWZsKjk1nlwrOmSMcfy8F_tn2v2ELtJcDLGWKNhO3Zwy5pAMl_l"
$userId = "1356296581472718988"
$ghToken = $env:GITHUB_TOKEN
$headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

$ragDbPath = "$HOME\.office-rag-db"
$ragExists = Test-Path $ragDbPath
$ragSize = if ($ragExists) { "{0:N1} MB" -f ((Get-ChildItem $ragDbPath -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB) } else { "Not found" }
$lastReindex = if (Test-Path "$ragDbPath\reindex.log") { (Get-Item "$ragDbPath\reindex.log").LastWriteTime.ToString("yyyy-MM-dd HH:mm") } else { "Never" }

$reviewedFile = "$HOME\.office-rag-db\reviewed-prs.json"
$reviewedCount = if (Test-Path $reviewedFile) { (Get-Content $reviewedFile | ConvertFrom-Json).Count } else { 0 }

$tasks = @("RAG-Nightly-Reindex", "Auto-PR-Review", "AIAutoPipeline")
$taskStatus = ($tasks | ForEach-Object {
    try {
        $t = Get-ScheduledTask -TaskName $_ -ErrorAction Stop
        "- **$_**: $($t.State)"
    } catch {
        "- **$_**: Not found"
    }
}) -join "`n"

$payload = @{
    content = "<@$userId>"
    embeds = @(
        @{
            title = "Pipeline Status"
            color = 16776960
            fields = @(
                @{ name = "RAG Database"; value = "Size: $ragSize`nLast reindex: $lastReindex"; inline = $true }
                @{ name = "Reviews"; value = "$reviewedCount PRs reviewed"; inline = $true }
                @{ name = "Scheduled Tasks"; value = $taskStatus; inline = $false }
            )
            footer = @{ text = "Requested via status command" }
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        }
    )
}

$jsonPayload = $payload | ConvertTo-Json -Depth 6 -Compress
Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json; charset=utf-8" -Body ([System.Text.Encoding]::UTF8.GetBytes($jsonPayload))
Write-Host "Status posted to Discord."
