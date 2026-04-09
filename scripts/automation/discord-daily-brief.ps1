$scoringModel = if ($env:FOUNDRY_SCORING_MODEL) { $env:FOUNDRY_SCORING_MODEL } else { "deepseek-r1:14b" }

$webhook = "https://discord.com/api/webhooks/1490590808603361291/SCVngVWu8BmQ87KBfwWZsKjk1nlwrOmSMcfy8F_tn2v2ELtJcDLGWKNhO3Zwy5pAMl_l"
$userId = "1356296581472718988"

try {
    $chatBody = @{
        model    = $scoringModel
        messages = @(@{ role = "user"; content = "Give me a daily brief. What should I focus on today?" })
        stream   = $false
    } | ConvertTo-Json -Depth 3

    $brief = Invoke-RestMethod -Uri "http://localhost:11434/api/chat" -Method POST -ContentType "application/json" -Body $chatBody

    $content = $brief.message.content

    $body = @{
        content = "<@$userId> ☀️ **Daily Brief -- $(Get-Date -Format 'yyyy-MM-dd HH:mm')**`n`n$content`n`n---`n_Sent from Ollama on Dustin_"
    } | ConvertTo-Json -EscapeHandling EscapeNonAscii

    Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json" -Body $body
}
catch {
    $errBody = @{
        content = "<@$userId> 🔴 **Daily Brief Failed** -- $(Get-Date -Format 'yyyy-MM-dd HH:mm')`n`n


