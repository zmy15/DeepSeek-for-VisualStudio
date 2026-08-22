# JSON-aware de-emoji sweep for localization files (v2).
# Loads via ConvertFrom-Json, transforms values, writes compact JSON back.
# Contract guard: values starting with the error/timeout marker chars are kept as-is.
# Pure ASCII source; PS 5.1 compatible.
param()

$ErrorActionPreference = 'Stop'
$files = @('Resources\Locales\zh-CN.json', 'Resources\Locales\en.json')

$allowedPrefixes = @(
    'ui.chat.', 'chat.html.', 'chat.windowTitle', 'chat.deleteConversation',
    'status.', 'agent.status.', 'agent.taskCancelled', 'agent.taskCompletedFiles',
    'agent.planCompletedSteps', 'agent.executionFailed', 'agent.explicitRoute.',
    'codeAction.', 'skills.loaded', 'skills.refreshed', 'skills.availableCommands',
    'skills.help.', 'skills.create.', 'inlineEdit.', 'settings.', 'mcp.dialog.'
)

$emoji = '[\uD83C-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF\u2B00-\u2BFF\u23E9-\u23FA\uFE0F\u200D\u20E3]'

foreach ($file in $files) {
    $full = Join-Path (Get-Location) $file
    $txt = [System.IO.File]::ReadAllText($full, [System.Text.Encoding]::UTF8)
    $json = ConvertFrom-Json $txt

    $changed = 0
    foreach ($p in $json.PSObject.Properties) {
        if ($p.Value -isnot [string]) { continue }
        $k = $p.Name; $v = $p.Value
        if ($v -notmatch '[\uD83C-\uDBFF\u2600-\u27BF\u2B00-\u2BFF\u23E9-\u23FA]') { continue }

        $allowed = $false
        foreach ($pref in $allowedPrefixes) {
            if ($k.StartsWith($pref, [System.StringComparison]::Ordinal)) { $allowed = $true; break }
        }
        # Tool-call chips are pure UI; everything else under tool.* may be model-facing
        if (-not $allowed -and $k -match '^tool\.[^.]+\.displayText') { $allowed = $true }
        if (-not $allowed) { continue }

        # Contract guard: keep parse anchors intact
        if ($v.StartsWith([char]0x274C) -or $v.StartsWith([char]0x23F1)) { continue }

        $cleaned = [regex]::Replace($v, $emoji, '')
        $cleaned = [regex]::Replace($cleaned, '\s+', ' ').Trim()
        if ($cleaned -eq '' -or $cleaned -eq $v) { continue }

        $json.PSObject.Properties[$k].Value = $cleaned
        $changed++
    }

    $out = $json | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($full, $out, (New-Object System.Text.UTF8Encoding($false)))

    # Validate immediately
    ConvertFrom-Json ([System.IO.File]::ReadAllText($full, [System.Text.Encoding]::UTF8)) | Out-Null
    Write-Host "$file : $changed values cleaned, JSON valid"
}
