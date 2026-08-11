[CmdletBinding()]
param(
    [string]$FranthropyDalamudProject
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$receiptPath = Join-Path $repositoryRoot 'Franthropy.commit'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "DAB's Franthropy consumer receipt is missing at '$receiptPath'."
}

$expectedCommit = (Get-Content -LiteralPath $receiptPath -Raw).Trim()
if ($expectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "DAB's Franthropy consumer receipt must contain one full Git commit."
}

if ([string]::IsNullOrWhiteSpace($FranthropyDalamudProject)) {
    $FranthropyDalamudProject = Join-Path $repositoryRoot '..\Franthropy\src\Franthropy.Dalamud\Franthropy.Dalamud.csproj'
}
$projectPath = [IO.Path]::GetFullPath($FranthropyDalamudProject)
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Franthropy.Dalamud was not found at '$projectPath'."
}

$candidate = Split-Path -Parent $projectPath
$franthropyRoot = $null
while (-not [string]::IsNullOrWhiteSpace($candidate)) {
    if (Test-Path -LiteralPath (Join-Path $candidate '.git')) {
        $franthropyRoot = $candidate
        break
    }
    $parent = Split-Path -Parent $candidate
    if ($parent -eq $candidate) { break }
    $candidate = $parent
}
if ([string]::IsNullOrWhiteSpace($franthropyRoot)) {
    throw "Franthropy.Dalamud must come from a Git checkout so DAB can verify its exact source revision."
}

$canonicalProject = [IO.Path]::GetFullPath((Join-Path $franthropyRoot 'src\Franthropy.Dalamud\Franthropy.Dalamud.csproj'))
if (-not [string]::Equals($projectPath, $canonicalProject, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The selected project is not the canonical Franthropy.Dalamud project in '$franthropyRoot'."
}

$head = (& git -C $franthropyRoot rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not resolve the Franthropy checkout revision at '$franthropyRoot'."
}
if (-not [string]::Equals($head, $expectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "DAB requires Franthropy '$expectedCommit', but '$franthropyRoot' is at '$head'."
}

$dirty = (& git -C $franthropyRoot status --porcelain=v1 --untracked-files=all | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace($dirty)) {
    throw "DAB connector builds require a clean Franthropy checkout at the pinned revision."
}

[pscustomobject]@{
    Dependency = 'Franthropy.Dalamud'
    Commit = $head
    Project = $projectPath
    Clean = $true
} | ConvertTo-Json -Compress
