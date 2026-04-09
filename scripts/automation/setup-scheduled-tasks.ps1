<#
.SYNOPSIS
    Registers all Office automation Windows Scheduled Tasks.

.DESCRIPTION
    Creates and registers the following scheduled tasks under the \Office\ folder:
      - RAG-Nightly-Reindex   : Re-indexes Office repo into the ChromaDB RAG database every 90 minutes.
      - Auto-PR-Review        : Reviews and scores Copilot PRs, auto-merges high-scoring ones.
      - AIAutoPipeline        : Creates AI-suggested GitHub issues from RAG context.
      - Health-Check          : Pings Ollama every 15 minutes and alerts Discord if down.
      - Daily-Brief           : Sends a morning brief via Ollama to Discord at 07:00.
      - Repo-Scan-Issues      : Scans repos for gaps and suggests new issues every 6 hours.
      - Office-ML-Retrain     : Retrains the PR scoring ML model nightly at 02:00.

    Run this script once from an elevated PowerShell prompt.
    Re-running is safe -- existing tasks are removed and re-created.

.PARAMETER ScriptsRoot
    Path to the Office/scripts directory. Defaults to the directory containing this script.

.PARAMETER CondaEnv
    Name of the conda environment for Python scripts. Defaults to 'office-scoring'.

.PARAMETER Force
    If specified, removes and re-creates tasks without prompting.

.EXAMPLE
    .\setup-scheduled-tasks.ps1
    .\setup-scheduled-tasks.ps1 -ScriptsRoot "C:\Users\koraj\OneDrive\Documents\GitHub\Office\scripts"
    .\setup-scheduled-tasks.ps1 -Force
#>

[CmdletBinding()]
param(
    [string]$ScriptsRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$CondaEnv = "office-scoring",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Resolve paths
$ScriptsDir = Join-Path $ScriptsRoot "scripts"
if (-not (Test-Path $ScriptsDir)) {
    # Fallback: assume this script IS in the scripts directory
    $ScriptsDir = $PSScriptRoot
}

$TaskFolder = "\Office\"
$Principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

# ──────────────────────────────────────────────
# Helper: Register or replace a single task
# ──────────────────────────────────────────────
function Register-OfficeTask {
    param(
        [string]$Name,
        [Microsoft.Management.Infrastructure.CimInstance]$Trigger,
        [Microsoft.Management.Infrastructure.CimInstance]$Action,
        [string]$Description,
        [Microsoft.Management.Infrastructure.CimInstance]$Settings = $null
    )

    $fullName = "$TaskFolder$Name"

    # Remove existing task if present
    try {
        $existing = Get-ScheduledTask -TaskPath $TaskFolder -TaskName $Name -ErrorAction Stop
        if ($existing) {
            if (-not $Force -and -not $PSCmdlet.ShouldContinue("Task '$Name' already exists. Replace it?", "Confirm")) {
                Write-Host "  SKIPPED: $Name (already exists)" -ForegroundColor Yellow
                return
            }
            Unregister-ScheduledTask -TaskPath $TaskFolder -TaskName $Name -Confirm:$false
            Write-Host "  Removed existing: $Name" -ForegroundColor DarkGray
        }
    } catch {
        # Task doesn't exist -- that's fine
    }

    $taskSettings = if ($Settings) { $Settings } else {
        New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -StartWhenAvailable `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 30)
    }

    Register-ScheduledTask `
        -TaskPath $TaskFolder `
        -TaskName $Name `
        -Trigger $Trigger `
        -Action $Action `
        -Principal $Principal `
        -Settings $taskSettings `
        -Description $Description `
        -Force:$Force | Out-Null

    Write-Host "  REGISTERED: $Name" -ForegroundColor Green
}

# ──────────────────────────────────────────────
# Validate prerequisites
# ──────────────────────────────────────────────
Write-Host "`n=== Office Scheduled Tasks Setup ===" -ForegroundColor Cyan
Write-Host "Scripts directory: $ScriptsDir"

$requiredScripts = @(
    "rag\reindex.ps1",
    "auto-pr-review.ps1",
    "auto-issue-pipeline.ps1",
    "health-check.ps1",
    "discord-daily-brief.ps1",
    "repo-scan-issues.ps1"
)

$missing = @()
foreach ($script in $requiredScripts) {
    $fullPath = Join-Path $ScriptsDir $script
    if (-not (Test-Path $fullPath)) {
        $missing += $script
    }
}

if ($missing.Count -gt 0) {
    Write-Warning "Missing scripts:`n  $($missing -join "`n  ")"
    Write-Warning "Tasks for missing scripts will still be registered but may fail until scripts are in place."
}

# Check conda / Python
$pythonOk = $false
try {
    $condaInfo = conda info --json 2>$null | ConvertFrom-Json
    $condaEnvs = conda env list --json 2>$null | ConvertFrom-Json
    $envExists = $condaEnvs.envs | Where-Object { $_ -match $CondaEnv }
    if ($envExists) {
        Write-Host "Conda environment '$CondaEnv' found." -ForegroundColor Green
        $pythonOk = $true
    } else {
        Write-Host "Conda environment '$CondaEnv' not found." -ForegroundColor Yellow
        Write-Host "Create it with: conda env create -f scripts/scoring/environment.yml" -ForegroundColor Yellow
    }
} catch {
    Write-Host "Conda not found. Python tasks will use system Python." -ForegroundColor Yellow
}

# Check GITHUB_TOKEN
if (-not $env:GITHUB_TOKEN) {
    Write-Warning "GITHUB_TOKEN environment variable is not set. GitHub API tasks will fail."
    Write-Warning "Set it with: [System.Environment]::SetEnvironmentVariable('GITHUB_TOKEN', 'ghp_...', 'User')"
}

Write-Host ""

# ──────────────────────────────────────────────
# 1. RAG Nightly Reindex -- every 90 minutes
# ──────────────────────────────────────────────
$ragTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 90)
$ragAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $ScriptsDir 'rag\reindex.ps1')`"" `
    -WorkingDirectory $ScriptsDir

Register-OfficeTask `
    -Name "RAG-Nightly-Reindex" `
    -Trigger $ragTrigger `
    -Action $ragAction `
    -Description "Re-indexes Office repo into the ChromaDB RAG database every 90 minutes."

# ──────────────────────────────────────────────
# 2. Auto-PR-Review -- every 30 minutes
# ──────────────────────────────────────────────
$prTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 30)
$prAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $ScriptsDir 'auto-pr-review.ps1')`"" `
    -WorkingDirectory $ScriptsDir

Register-OfficeTask `
    -Name "Auto-PR-Review" `
    -Trigger $prTrigger `
    -Action $prAction `
    -Description "Reviews Copilot PRs, runs the scoring preprocessor, and auto-merges high-confidence PRs."

# ──────────────────────────────────────────────
# 3. AI Auto Pipeline -- every 3 hours
# ──────────────────────────────────────────────
$issueTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Hours 3)
$issueAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $ScriptsDir 'auto-issue-pipeline.ps1')`"" `
    -WorkingDirectory $ScriptsDir

Register-OfficeTask `
    -Name "AIAutoPipeline" `
    -Trigger $issueTrigger `
    -Action $issueAction `
    -Description "Creates AI-suggested GitHub issues using Ollama + RAG context every 3 hours."

# ──────────────────────────────────────────────
# 4. Health Check -- every 15 minutes
# ──────────────────────────────────────────────
$healthTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 15)
$healthAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $ScriptsDir 'health-check.ps1')`"" `
    -WorkingDirectory $ScriptsDir

$healthSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)

Register-OfficeTask `
    -Name "Health-Check" `
    -Trigger $healthTrigger `
    -Action $healthAction `
    -Description "Pings Ollama every 15 minutes and alerts Discord if the service is down or degraded." `
    -Settings $healthSettings

# ──────────────────────────────────────────────
# 5. Daily Brief -- every morning at 07:00
# ──────────────────────────────────────────────
$briefTrigger = New-ScheduledTaskTrigger -Daily -At "07:00"
$briefAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $ScriptsDir 'discord-daily-brief.ps1')`"" `
    -WorkingDirectory $ScriptsDir

Register-OfficeTask `
    -Name "Daily-Brief" `
    -Trigger $briefTrigger `
    -Action $briefAction `
    -Description "Sends a morning daily brief via Ollama to the Discord webhook at 07:00."

# ──────────────────────────────────────────────
# 6. Repo Scan Issues -- every 6 hours
# ──────────────────────────────────────────────
$scanTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Hours 6)
$scanAction = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $ScriptsDir 'repo-scan-issues.ps1')`"" `
    -WorkingDirectory $ScriptsDir

Register-OfficeTask `
    -Name "Repo-Scan-Issues" `
    -Trigger $scanTrigger `
    -Action $scanAction `
    -Description "Scans both repos for coverage gaps and suggests new issues every 6 hours."

# ──────────────────────────────────────────────
# 7. ML Retrain -- nightly at 02:00
# ──────────────────────────────────────────────
$retrainTrigger = New-ScheduledTaskTrigger -Daily -At "02:00"
$retrainScript = Join-Path $ScriptsDir "scoring\retrain.py"
$repoRoot = Split-Path -Parent $ScriptsDir

if ($pythonOk) {
    $retrainAction = New-ScheduledTaskAction `
        -Execute "cmd.exe" `
        -Argument "/C conda activate $CondaEnv && python `"$retrainScript`"" `
        -WorkingDirectory $repoRoot
} else {
    $retrainAction = New-ScheduledTaskAction `
        -Execute "python" `
        -Argument "`"$retrainScript`"" `
        -WorkingDirectory $repoRoot
}

$retrainSettings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 15)

Register-OfficeTask `
    -Name "Office-ML-Retrain" `
    -Trigger $retrainTrigger `
    -Action $retrainAction `
    -Description "Retrains the PR scoring GradientBoosting model nightly from accumulated decision memory. Saves model to State/ml-artifacts/." `
    -Settings $retrainSettings

# ──────────────────────────────────────────────
# Summary
# ──────────────────────────────────────────────
Write-Host "`n=== Setup Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Registered tasks under $TaskFolder :"
try {
    Get-ScheduledTask -TaskPath $TaskFolder | Format-Table TaskName, State, @{
        Label = "NextRun"
        Expression = {
            try {
                (Get-ScheduledTaskInfo -TaskPath $TaskFolder -TaskName $_.TaskName).NextRunTime
            } catch { "N/A" }
        }
    } -AutoSize
} catch {
    Write-Host "  (Run 'Get-ScheduledTask -TaskPath `"$TaskFolder`"' to verify)"
}

Write-Host @"

NEXT STEPS:
  1. Verify tasks:  Get-ScheduledTask -TaskPath '\Office\'
  2. Test a task:    Start-ScheduledTask -TaskPath '\Office\' -TaskName 'Health-Check'
  3. Check logs:     Get-Content `$HOME\.office-rag-db\reindex.log -Tail 20
  4. View status:    powershell -File '$ScriptsDir\commands\status.ps1'

CONDA SETUP (if not done):
  conda env create -f "$ScriptsDir\scoring\environment.yml"
  conda activate $CondaEnv
"@
