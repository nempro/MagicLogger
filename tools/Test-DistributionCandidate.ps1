param([Parameter(Mandatory)][string]$CandidateDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$allowed = @(Get-Content -LiteralPath (Join-Path $root 'distribution-allowlist.txt') |
    Where-Object { $_ -and -not $_.StartsWith('#') } |
    ForEach-Object { $_.Replace('/', '\') })
$allowedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $allowed) { [void]$allowedSet.Add($name) }
$allowedDirectories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $allowed) {
    $parent = Split-Path -Parent $name
    while ($parent) {
        [void]$allowedDirectories.Add($parent)
        $parent = Split-Path -Parent $parent
    }
}
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in Get-ChildItem -LiteralPath $candidate -Force -Recurse) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Link is not permitted: $($entry.FullName)" }
    $relative = $entry.FullName.Substring($candidate.Length).TrimStart('\')
    if ($entry.PSIsContainer) {
        if (-not $allowedDirectories.Contains($relative)) { throw "Unexpected distribution directory: $relative" }
        continue
    }
    if (-not $allowedSet.Contains($relative)) { throw "Unexpected distribution file: $relative" }
    if ($entry.Length -eq 0) { throw "Empty distribution file: $relative" }
    [void]$seen.Add($relative)
}
foreach ($name in $allowed) {
    if (-not $seen.Contains($name)) { throw "Missing distribution file: $name" }
}
$textFiles = @('MagicLogger.deps.json','MagicLogger.runtimeconfig.json','README.md','LICENSE',
    'Start-MagicLogger.cmd','THIRD-PARTY-NOTICES.txt','THIRD-PARTY-LICENSES/MIT.txt',
    'THIRD-PARTY-LICENSES/Apache-2.0.txt')
foreach ($name in $textFiles) {
    $content = Get-Content -LiteralPath (Join-Path $candidate $name) -Raw
    if ($content -match '(?i)[A-Z]:[\\/](Users|Documents and Settings)[\\/][^\\/\s]+' -or
        $content -match '(?i)(api[_-]?key|password|credential|secret)\s*[:=]\s*["''][^"'']{8,}') {
        throw "Potential private path or credential in $name"
    }
}

# Assemblies can retain absolute PDB paths even when no .pdb is distributed.
$privateMarkers = '(?i)(?:[A-Z]:\\(?:Users|product)\\|\.runtime-data|source-exports|sk-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{36}|AKIA[A-Z0-9]{16}|(?:api[_-]?key|password|credential|secret)\s*[:=]\s*["''][^"'']{8,})'
foreach ($file in Get-ChildItem -LiteralPath $candidate -File -Recurse) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $asUtf8 = [Text.Encoding]::UTF8.GetString($bytes)
    $asUtf16 = [Text.Encoding]::Unicode.GetString($bytes)
    if ($asUtf8 -match $privateMarkers -or $asUtf16 -match $privateMarkers) {
        throw "Potential private marker in distribution file: $($file.FullName.Substring($candidate.Length + 1))"
    }
}
$config = Get-Content -LiteralPath (Join-Path $candidate 'MagicLogger.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($config.runtimeOptions.tfm -ne 'net10.0' -and $config.runtimeOptions.tfm -ne 'net10.0-windows') {
    throw 'Unexpected target framework.'
}
$frameworks = @($config.runtimeOptions.frameworks | ForEach-Object { $_.name })
if (@($frameworks | Sort-Object) -join ',' -ne 'Microsoft.NETCore.App,Microsoft.WindowsDesktop.App') {
    throw 'Expected framework-dependent .NET Desktop runtime configuration.'
}
$dependencies = (Get-Content -LiteralPath (Join-Path $candidate 'MagicLogger.deps.json') -Raw | ConvertFrom-Json).libraries.PSObject.Properties.Name
$thirdParty = @($dependencies | Where-Object { $_ -notlike 'MagicLogger/*' } | Sort-Object)
$expected = @('Microsoft.Data.Sqlite/10.0.11','Microsoft.Data.Sqlite.Core/10.0.11',
    'SQLitePCLRaw.bundle_e_sqlite3/2.1.12','SQLitePCLRaw.core/2.1.12',
    'SQLitePCLRaw.lib.e_sqlite3/2.1.12','SQLitePCLRaw.provider.e_sqlite3/2.1.12' | Sort-Object)
if (($thirdParty -join ',') -ne ($expected -join ',')) {
    throw 'Dependency list changed; update licenses and the allowlist before distribution.'
}
Write-Output "PASS: $($seen.Count) allowlisted files, no unexpected files or text signatures."
