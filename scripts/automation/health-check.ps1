$webhook = "https://discord.com/api/webhooks/1490590808603361291/SCVngVWu8BmQ87KBfwWZsKjk1nlwrOmSMcfy8F_tn2v2ELtJcDLGWKNhO3Zwy5pAMl_l"
$userId = "1356296581472718988"

try {
    $health = Invoke-RestMethod -Uri "http://localhost:11434/api/tags" -TimeoutSec 10

    if (-not $health.models -or $health.models.Count -eq 0) {
        $body = @{ content = "<@$userId> ⚠️ **Ollama Degraded** -- $(Get-Date -Format 'HH:mm')`n`nNo models loaded." } | ConvertTo-Json -EscapeHandling EscapeNonAscii
        Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json" -Body $body
    }
}
catch {
    $body = @{ content = "<@$userId> 🔴 **Ollama is DOWN** -- $(Get-Date -Format 'HH:mm')`n`nError: $($_.Exception.Message)" } | ConvertTo-Json -EscapeHandling EscapeNonAscii
    Invoke-RestMethod -Uri $webhook -Method POST -ContentType "application/json" -Body $body
}