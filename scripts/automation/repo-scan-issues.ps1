$webhook = "https://discord.com/api/webhooks/1490590808603361291/SCVngVWu8BmQ87KBfwWZsKjk1nlwrOmSMcfy8F_tn2v2ELtJcDLGWKNhO3Zwy5pAMl_l"
$userId = "1356296581472718988"
$ghToken = $env:GITHUB_TOKEN
$suggestionsFile = "$HOME\.office-suggestions.json"

try {
    $headers = @{ Authorization = "Bearer $ghToken"; Accept = "application/vnd.github.v3+json" }

    $officeIssues = (Invoke-RestMethod -Uri "https://api.github.com/repos/Koraji95-coder/Office/issues?state=open&per_page=10" -Headers $headers) | ForEach-Object { "- #$($_.number): $($_.title)" }

    $context = @"
Here are the currently open issues:

OFFICE REPO:
$($officeIssues -join "`n")
"@

    $prompt = @"
You are a technical project manager. Review the open issues above and suggest 1-3 NEW issues that should be created. Focus on:
- Gaps in test coverage
- Missing documentation
- Technical debt or hardening
- Features that would improve the automation pipeline

For each suggestion, respond ONLY with valid JSON. No markdown, no explanation. Just a JSON array:
[
  { "repo": "Office", "title": "issue title here", "body": "description here" }
]

Do NOT suggest issues that already exist. Only suggest high-value work.

$context
"@

    $chatBody = @{
        model    = "qwen3:14b"
        messages = @(@{ role = "user"; content = $prompt })
        stream   = $false
    } | ConvertTo-Json -Depth 3

    $response = Invoke-RestMethod -Uri "http://localhost:11434/api/chat" -Method POST -ContentType "application/json" -Body $chatBody

    $content = $response.message.content

    if ($content -match '\[[\s\S]*\]') {
        $jsonText = $Matches[0]
        $suggestions = $jsonText | ConvertFrom-Json
        $suggestions | ConvertTo-Json -Depth 5 | Set-Content $suggestionsFile

        $colors = @{
            "Office" = 16744256
        }

        $embeds = @()
        for ($i = 0; $i -lt $suggestions.Count; $i++) {
            $s = $suggestions[$i]

            # Suite excluded while building ML training base
            if ($s.repo -eq "Suite") { continue }

            $repoColor = if ($colors.ContainsKey($s.repo)) { $colors[$s.repo] } else { 8421504 }

            $embeds += @{
                title       = "#$($i + 1) $($s.title)"
                description = $s.body
                color       = $repoColor
                footer      = @{ text = $s.repo }
            }
        }

        $payload = @{
            content = "<@$userId> **AI Issue Suggestions** -- Run ``Approve-Issues 1 2 3`` to create."
            embeds  = $embeds
        }

        $jsonPayload = $payload | ConvertTo-Json -Depth 5 -Compress
        Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json; charset=utf-8" -Body ([System.Text.Encoding]::UTF8.GetBytes($jsonPayload))
    }
    else {
        $body = @{
            content = "<@$userId> **AI Issue Suggestions** -- Could not auto-parse. Create issues manually.`n`n$content"
        } | ConvertTo-Json -EscapeHandling EscapeNonAscii

        Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json" -Body $body
    }
}
catch {
    $errBody = @{
        content = "<@$userId> **Repo Scan Failed** -- $(Get-Date -Format 'HH:mm') -- $($_.Exception.Message)"
    } | ConvertTo-Json -EscapeHandling EscapeNonAscii

    Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json" -Body $errBody
}

