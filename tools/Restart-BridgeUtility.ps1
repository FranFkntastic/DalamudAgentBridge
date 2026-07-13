[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$InstanceName = 'secondary',

    [ValidateRange(1024, 65535)]
    [int]$Port = 45832,

    [string]$PluginConfigRoot = "$env:APPDATA\XIVLauncher-Multibox-2\pluginConfigs",

    [switch]$NoBuild,

    [switch]$DisableSharedCompilation
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\DalamudAgentBridge\DalamudAgentBridge.csproj'
$staging = Join-Path $repoRoot "artifacts\utility-$InstanceName-next"
$target = Join-Path $repoRoot "artifacts\utility-$InstanceName"
$logs = Join-Path $repoRoot 'artifacts\logs'
$buildLog = Join-Path $logs "utility-$InstanceName-build.log"

New-Item -ItemType Directory -Path $logs -Force | Out-Null
if (-not $NoBuild) {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    & dotnet build $project -c Release -o $staging --no-restore -p:UseSharedCompilation=$(-not $DisableSharedCompilation) *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $buildLog -Tail 120
        throw "Bridge utility build failed. Full output: $buildLog"
    }
}

$source = if ($NoBuild) { $target } else { $staging }
$sourceDll = Join-Path $source 'DalamudAgentBridge.dll'
if (-not (Test-Path -LiteralPath $sourceDll)) {
    throw "Bridge utility output was not found: $sourceDll"
}
$resolvedRepo = (Resolve-Path -LiteralPath $repoRoot).Path
$resolvedSource = (Resolve-Path -LiteralPath $source).Path
if (-not $resolvedSource.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The bridge source directory escaped the repository.'
}

$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($listener) {
    Stop-Process -Id $listener.OwningProcess -Force
    Wait-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
}

if (-not $NoBuild) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $resolvedTarget = (Resolve-Path -LiteralPath $target).Path
    if (-not $resolvedTarget.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The bridge deployment directory escaped the repository.'
    }
    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
}

$bridgeDll = Join-Path $target 'DalamudAgentBridge.dll'
if ((Get-FileHash -LiteralPath $bridgeDll -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash) {
    throw 'The staged and deployed bridge DLL hashes differ.'
}

$arguments = @(
    "`"$bridgeDll`"",
    "--Bridge:Url=http://127.0.0.1:$Port",
    "--Bridge:PluginConfigRoot=$PluginConfigRoot"
)
$process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $target -WindowStyle Hidden -PassThru
$deadline = (Get-Date).AddSeconds(15)
do {
    Start-Sleep -Milliseconds 200
    if ($process.HasExited) {
        throw "Bridge utility exited with code $($process.ExitCode)."
    }
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
} until ($listener -or (Get-Date) -ge $deadline)
if (-not $listener) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Bridge utility PID $($process.Id) did not listen on port $Port."
}

[pscustomobject]@{
    Instance = $InstanceName
    Port = $Port
    ProcessId = $listener.OwningProcess
    Dll = $bridgeDll
    Sha256 = (Get-FileHash -LiteralPath $bridgeDll -Algorithm SHA256).Hash
    BuildLog = $buildLog
}
