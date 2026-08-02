param(
    [switch]$NoBuild,
    [switch]$BuildPluginRepository,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$InstanceName = 'primary',
    [ValidateRange(1024, 65535)]
    [int]$Port = 45831,
    [string]$PluginConfigRoot
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\DalamudAgentBridge\DalamudAgentBridge.csproj'
$output = Join-Path $PSScriptRoot "artifacts\utility-$InstanceName"
$bridgeDll = Join-Path $output 'DalamudAgentBridge.dll'

if ($NoBuild -and $BuildPluginRepository) {
    throw '-BuildPluginRepository cannot be combined with -NoBuild.'
}

if (-not $NoBuild) {
    if ($BuildPluginRepository) {
        & (Join-Path $PSScriptRoot 'Build-PluginRepository.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Plugin repository build failed.' }
    }
    dotnet build $project -c Release -o $output
    if ($LASTEXITCODE -ne 0) { throw "Bridge utility build failed for instance '$InstanceName'." }
}

if (-not (Test-Path -LiteralPath $bridgeDll)) {
    throw "Bridge utility output was not found for instance '$InstanceName': $bridgeDll"
}

$arguments = @($bridgeDll, "--Bridge:Url=http://127.0.0.1:$Port")
if (-not [string]::IsNullOrWhiteSpace($PluginConfigRoot)) {
    $arguments += "--Bridge:PluginConfigRoot=$PluginConfigRoot"
}

Write-Host "Dalamud Agent Bridge [$InstanceName]: http://127.0.0.1:$Port"
& dotnet @arguments
