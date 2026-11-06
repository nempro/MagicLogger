param(
    [Parameter(Mandatory)][string]$ApplicationOutputDirectory,
    [Parameter(Mandatory)][string]$CandidateDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = (Resolve-Path -LiteralPath $ApplicationOutputDirectory).Path
$destination = [IO.Path]::GetFullPath($CandidateDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Candidate directory already exists; refusing to mix with existing files.' }
$docs = @('Start-MagicLogger.cmd','README.md','LICENSE','THIRD-PARTY-NOTICES.txt',
    'THIRD-PARTY-LICENSES/MIT.txt','THIRD-PARTY-LICENSES/Apache-2.0.txt')
$allowed = @(Get-Content -LiteralPath (Join-Path $root 'distribution-allowlist.txt') |
    Where-Object { $_ -and -not $_.StartsWith('#') })
foreach ($name in $allowed) {
    $sourceRoot = if ($docs -contains $name) { $root } else { $app }
    $source = Join-Path $sourceRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required source file missing: $name" }
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Source link is not permitted: $name" }
}
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($name in $allowed) {
    $sourceRoot = if ($docs -contains $name) { $root } else { $app }
    $target = Join-Path $destination $name
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $target
}
& (Join-Path $PSScriptRoot 'Test-DistributionCandidate.ps1') -CandidateDirectory $destination
