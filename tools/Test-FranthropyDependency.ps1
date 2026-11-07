[CmdletBinding()]
param([string]$FranthropyDalamudProject, [string]$FranthropyFeed = (Join-Path $env:LOCALAPPDATA 'Franthropy/packages'))
$ErrorActionPreference = 'Stop'
if ($FranthropyDalamudProject) { throw 'Use MSBuild FranthropySource=Local with FranthropyRoot for paired development.' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
[xml]$pin = Get-Content (Join-Path $repositoryRoot 'eng/Franthropy.Version.props')
$version = [string]$pin.Project.PropertyGroup.FranthropyPackageVersion
if ($version -notmatch '^0\.1\.0-commit\.([0-9a-f]{40})$') { throw 'Invalid Franthropy package pin.' }
$commit = $Matches[1]
$package = Join-Path $FranthropyFeed "Franthropy.Dalamud.$version.nupkg"
if (-not (Test-Path -LiteralPath $package)) { throw "Pinned Franthropy package is missing: $package" }
[pscustomobject]@{ Dependency = 'Franthropy.Dalamud'; Commit = $commit; Package = $package; Version = $version } | ConvertTo-Json -Compress
