param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DalamudAgentBridge\DalamudAgentBridge.csproj'

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot 'Build-PluginRepository.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Plugin repository build failed.' }
    dotnet build $project
}

Write-Host 'Dalamud Agent Bridge: http://127.0.0.1:45831'
dotnet run --project $project --no-build
