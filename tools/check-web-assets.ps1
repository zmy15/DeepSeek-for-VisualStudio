<#
.SYNOPSIS
    校验 WebView 前端资源（Resources\Chat 下的 JS / CSS）的编码与语法。

.DESCRIPTION
    对每个嵌入的 .js 资源执行 node 语法检查与占位符配对检查，并统一校验编码。
    设计要点：
      1. 占位符 {{key}} 在替换前不是合法 JS 值，检查前先临时替换为合法标识符，
         这样脚本文件本身既能被人读懂，又能被语法解析器接受。
      2. 未安装 Node 时降级为警告并返回 0，不阻断提交——语法检查是增强，不是准入前提。
      3. 读取必须显式 -Encoding UTF8，否则中文注释会按 ANSI 解码并产生假的语法错误。

.PARAMETER Files
    pre-commit 传入的待检查文件列表。为空时扫描整个 Resources\Chat 目录。

.EXAMPLE
    pwsh -NoProfile -File tools/check-web-assets.ps1
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Files
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$chatRoot = Join-Path $repoRoot 'Resources\Chat'
$placeholderPattern = '\{\{[A-Za-z0-9_]+\}\}'
$failures = New-Object System.Collections.Generic.List[string]

# ── 收集待检查文件：显式传入优先，否则扫描整个资源目录 ──
$targets = @()
if ($Files -and $Files.Count -gt 0) {
    $targets = $Files | Where-Object { $_ -match '\.(js|css)$' } |
        ForEach-Object { if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $repoRoot $_ } }
} elseif (Test-Path $chatRoot) {
    $targets = Get-ChildItem -Path $chatRoot -Recurse -Include '*.js', '*.css' -File | Select-Object -ExpandProperty FullName
}

if (-not $targets -or $targets.Count -eq 0) {
    Write-Host '[check-web-assets] 未发现待检查资源，跳过。'
    exit 0
}

$node = Get-Command node -ErrorAction SilentlyContinue
$hasNode = $null -ne $node

foreach ($path in $targets) {
    if (-not (Test-Path $path)) { continue }

    $relative = $path.Substring($repoRoot.Length).TrimStart('\', '/')

    # ── 编码检查：BOM 会让某些解析器把首字符解析失败 ──
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $failures.Add("$relative : 文件包含 UTF-8 BOM，请以无 BOM 保存")
    }

    # 显式 UTF8 读取：避免中文被按 ANSI 解码后误报语法错误
    $text = Get-Content -Path $path -Raw -Encoding UTF8

    # ── 占位符配对检查：落单的 {{ 说明写法有误，运行期会残留到前端 ──
    $openCount = ([regex]::Matches($text, '\{\{')).Count
    $closedCount = ([regex]::Matches($text, $placeholderPattern)).Count
    if ($openCount -ne $closedCount) {
        $failures.Add("$relative : 占位符未闭合或 key 非法（出现 $openCount 个 '{{'，合法占位符 $closedCount 个）")
    }

    # ── 缩进混排检查：制表符与空格混用会让 diff 难以阅读 ──
    if ($text -match "(?m)^\t" -and $text -match '(?m)^ {2,}') {
        $failures.Add("$relative : 同时使用制表符与空格缩进")
    }

    if (-not $hasNode -or $path -notlike '*.js') { continue }

    # ── node 语法检查：占位符先替换为合法标识符，使文件成为可直接解析的脚本 ──
    $probe = [regex]::Replace($text, $placeholderPattern, '__WEBASSET_PLACEHOLDER__')
    $tempFile = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "webasset-$([guid]::NewGuid().ToString('N')).js")
    try {
        [System.IO.File]::WriteAllText($tempFile, $probe, (New-Object System.Text.UTF8Encoding($false)))
        $checkOutput = & node --check $tempFile 2>&1
        if ($LASTEXITCODE -ne 0) {
            $detail = ($checkOutput | Out-String).Trim()
            $failures.Add("$relative : node 语法检查失败`n$detail")
        }
    } finally {
        if (Test-Path $tempFile) { Remove-Item $tempFile -Force -ErrorAction SilentlyContinue }
    }
}

if (-not $hasNode) {
    Write-Warning '[check-web-assets] 未检测到 node，已跳过 JS 语法检查（占位符与编码检查仍已执行）。'
}

if ($failures.Count -gt 0) {
    Write-Host '[check-web-assets] 发现以下问题：' -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host "[check-web-assets] 通过：已检查 $($targets.Count) 个资源文件。"
exit 0
