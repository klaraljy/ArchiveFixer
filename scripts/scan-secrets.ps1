#Requires -Version 5.1
<#
.SYNOPSIS
    Fail when tracked files contain common private-key or token signatures.

.DESCRIPTION
    This is a conservative repository scan, not a full secret scanner.
    It intentionally checks only high-signal patterns so synthetic test passwords
    in the test suite do not create false positives.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

function Fail([string]$Text) {
    Write-Host ""
    Write-Host "[FAIL] $Text" -ForegroundColor Red
    exit 1
}

try {
    $patterns = @(
        'BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY',
        'ghp_[A-Za-z0-9]{36}',
        'github_pat_[A-Za-z0-9_]{20,}',
        'AKIA[0-9A-Z]{16}',
        'xox[baprs]-[A-Za-z0-9-]{10,}'
    )

    foreach ($pattern in $patterns) {
        $hits = @(& git grep -I -n -E $pattern -- . 2>$null)
        if ($LASTEXITCODE -eq 0 -and $hits.Count -gt 0) {
            $hits | Select-Object -First 10 | ForEach-Object { Write-Host $_ }
            Fail "Tracked file contains a secret-like pattern: $pattern"
        }
        if ($LASTEXITCODE -notin @(0, 1)) {
            Fail "git grep failed for pattern: $pattern"
        }
    }

    $forbidden = @(& git ls-files | Where-Object {
        $_ -match '(^|/)(\.env|\.env\..*|id_rsa|id_ed25519)$' -or
        $_ -match '\.(pfx|p12|pem|key)$'
    })
    if ($forbidden.Count -gt 0) {
        $forbidden | Select-Object -First 10 | ForEach-Object { Write-Host $_ }
        Fail "Tracked key/environment files found."
    }

    Write-Host "[PASS] secret scan" -ForegroundColor Green
}
finally {
    Pop-Location
}
