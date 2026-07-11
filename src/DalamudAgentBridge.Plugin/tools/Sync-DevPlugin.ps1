param(
    [Parameter(Mandatory = $true)] [string]$SourceDir,
    [Parameter(Mandatory = $true)] [string]$DestDir,
    [Parameter(Mandatory = $true)] [string]$PluginName
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null

$required = @("$PluginName.dll", "$PluginName.json")
foreach ($file in $required) {
    $source = Join-Path $SourceDir $file
    if (-not (Test-Path -LiteralPath $source)) { throw "Required build output not found: $source" }
    Copy-Item -LiteralPath $source -Destination $DestDir -Force
}

Get-ChildItem -LiteralPath $SourceDir -File |
    Where-Object { ($_.Extension -in @('.dll', '.pdb', '.xml') -or $_.Name -like '*.deps.json') -and $_.Name -notin $required } |
    Copy-Item -Destination $DestDir -Force

Write-Host "Synced independent agent bridge plugin to $DestDir"
