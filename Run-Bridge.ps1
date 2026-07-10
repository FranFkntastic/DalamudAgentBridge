param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DalamudAgentBridge\DalamudAgentBridge.csproj'

if (-not $NoBuild) {
    dotnet build $project
}

Write-Host 'Dalamud Agent Bridge: http://127.0.0.1:45831'
dotnet run --project $project --no-build
