# ============================================================================
# clean-local-orphan-branches.ps1 - delete local branches missing on remote
#
# Usage:
#   powershell -File tools\clean-local-orphan-branches.ps1             # dry-run
#   powershell -File tools\clean-local-orphan-branches.ps1 -Apply      # delete
#   powershell -File tools\clean-local-orphan-branches.ps1 -Remote upstream
#
# Behavior:
#   1. Runs `git fetch --prune` so deleted remote branches are pruned.
#   2. Lists local branches that have no matching remote branch.
#   3. Dry-run by default; pass -Apply to delete (prompts unless -Force).
#   4. Never deletes the currently checked-out branch or branches whose
#      remote counterpart still exists.
#
# NOTE: keep this file ASCII-only (PS 5.1 parses BOM-less scripts as ANSI).
# ============================================================================
param(
    [string]$Remote = 'origin',
    [switch]$Apply,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
    & git @Args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Args -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "== Pruning remote-tracking refs for '$Remote' =="
    Invoke-Git fetch --prune $Remote

    $current = (Invoke-Git branch --show-current).Trim()
    $localBranches = Invoke-Git branch --format='%(refname:short)' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 }

    $orphans = @()
    foreach ($branch in $localBranches) {
        if ($branch -eq $current) { continue }
        $remoteRef = "refs/remotes/$Remote/$branch"
        git show-ref --verify --quiet $remoteRef
        if ($LASTEXITCODE -ne 0) {
            $orphans += $branch
        }
    }

    if ($orphans.Count -eq 0) {
        Write-Host "No local branches are missing on remote '$Remote'."
        return
    }

    Write-Host ""
    Write-Host "== Local branches missing on remote '$Remote' =="
    $orphans | ForEach-Object { Write-Host "  $_" }
    Write-Host "  ($($orphans.Count) branches)"

    if (-not $Apply) {
        Write-Host ""
        Write-Host "Dry run - pass -Apply to delete these branches."
        return
    }

    if (-not $Force) {
        $answer = Read-Host "Delete $($orphans.Count) branch(es)? [y/N]"
        if ($answer -notin @('y', 'Y', 'yes', 'YES')) {
            Write-Host "Aborted."
            return
        }
    }

    foreach ($branch in $orphans) {
        git branch -D $branch
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Failed to delete branch '$branch'."
        } else {
            Write-Host "Deleted local branch: $branch"
        }
    }
} finally {
    Pop-Location
}
