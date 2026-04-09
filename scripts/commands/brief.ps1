$webhook = "https://discord.com/api/webhooks/1490590808603361291/SCVngVWu8BmQ87KBfwWZsKjk1nlwrOmSMcfy8F_tn2v2ELtJcDLGWKNhO3Zwy5pAMl_l"
$userId = "1356296581472718988"
$ghToken = $env:GITHUB_TOKEN
$headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

$officePRs = Invoke-RestMethod -Uri "https://api.github.com/repos/Koraji95-coder/Foundry/pulls?state=open&per_page=10" -Headers $headers
$officeIssues = Invoke-RestMethod -Uri "https://api.github.com/repos/Koraji95-coder/Foundry/issues?state=open&per_page=20&labels=ai-suggested" -Headers $headers

$officePRList = ($officePRs | ForEach-Object { "- [#$($_.number)]($($_.html_url)) $($_.title) $(if($_.draft){'(draft)'})" }) -join "`n"
$officeIssueList = ($officeIssues | ForEach-Object { "- [#$($_.number)]($($_.html_url)) $($_.title)" }) -join "`n"

if (-not $officePRList) { $officePRList = "None" }
if (-not $officeIssueList) { $officeIssueList = "None" }

$payload = @{
    content = "<@$userId>"
    embeds = @(
        @{
            title = "Daily Brief"
            color = 5793266
            fields = @(
                @{ name = "Foundry PRs ($($officePRs.Count))"; value = $officePRList; inline = $false }
                @{ name = "Foundry Issues ($($officeIssues.Count)/20)"; value = $officeIssueList; inline = $false }
            )
            footer = @{ text = "Requested via brief command" }
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        }
    )
}

$jsonPayload = $payload | ConvertTo-Json -Depth 6 -Compress
Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json; charset=utf-8" -Body ([System.Text.Encoding]::UTF8.GetBytes($jsonPayload))
Write-Host "Brief posted to Discord."
