param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$FranthropyDalamudProject,

    [string]$FranthropyAgentBridgeProject
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "release\v$Version"))
if (-not $releaseRoot.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release path escaped the artifacts directory: $releaseRoot"
}

$pluginProject = Join-Path $repositoryRoot 'src\DalamudAgentBridge.Plugin\DalamudAgentBridge.Plugin.csproj'
$pluginManifest = Join-Path $repositoryRoot 'src\DalamudAgentBridge.Plugin\DalamudAgentBridge.json'
[xml]$projectXml = Get-Content -Raw -LiteralPath $pluginProject
$projectVersionText = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
$manifest = Get-Content -Raw -LiteralPath $pluginManifest | ConvertFrom-Json
$requestedVersion = [Version]::Parse($Version)
$projectVersion = [Version]::Parse($projectVersionText)
$manifestVersion = [Version]::Parse([string]$manifest.AssemblyVersion)
foreach ($declared in @($projectVersion, $manifestVersion)) {
    if ($declared.Major -ne $requestedVersion.Major -or
        $declared.Minor -ne $requestedVersion.Minor -or
        $declared.Build -ne $requestedVersion.Build) {
        throw "Release version $Version does not match declared plugin version $declared."
    }
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
$utilityStaging = Join-Path $releaseRoot 'utility'
$mcpStaging = Join-Path $releaseRoot 'mcp'
New-Item -ItemType Directory -Force -Path $utilityStaging, $mcpStaging | Out-Null

$pluginBuildArguments = @{
    Configuration = 'Release'
}
if ($FranthropyDalamudProject) {
    $pluginBuildArguments.FranthropyDalamudProject = $FranthropyDalamudProject
}
& (Join-Path $repositoryRoot 'Build-PluginRepository.ps1') @pluginBuildArguments
if ($LASTEXITCODE -ne 0) { throw 'Plugin package build failed.' }

$utilityPublishArguments = @(
    'publish',
    (Join-Path $repositoryRoot 'src\DalamudAgentBridge\DalamudAgentBridge.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $utilityStaging,
    "-p:Version=$Version"
)
if ($FranthropyAgentBridgeProject) {
    $utilityPublishArguments += "-p:FranthropyAgentBridgeProject=$FranthropyAgentBridgeProject"
}
dotnet @utilityPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'Utility publish failed.' }

$mcpPublishArguments = @(
    'publish',
    (Join-Path $repositoryRoot 'src\dab-mcp\dab-mcp.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $mcpStaging,
    "-p:Version=$Version"
)
if ($FranthropyAgentBridgeProject) {
    $mcpPublishArguments += "-p:FranthropyAgentBridgeProject=$FranthropyAgentBridgeProject"
}
dotnet @mcpPublishArguments
if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed.' }

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $utilityStaging
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $utilityStaging
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\mcp-setup.md') -Destination $mcpStaging
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $mcpStaging

$utilityArchive = Join-Path $releaseRoot 'DalamudAgentBridge-utility-win-x64.zip'
$mcpArchive = Join-Path $releaseRoot 'dab-mcp-win-x64.zip'
$pluginArchive = Join-Path $releaseRoot 'DalamudAgentBridge-plugin.zip'
Compress-Archive -Path (Join-Path $utilityStaging '*') -DestinationPath $utilityArchive -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $mcpStaging '*') -DestinationPath $mcpArchive -CompressionLevel Optimal
Copy-Item -LiteralPath (
    Join-Path $repositoryRoot 'src\DalamudAgentBridge\wwwroot\repository\DalamudAgentBridge.zip'
) -Destination $pluginArchive

$archives = @($utilityArchive, $mcpArchive, $pluginArchive)
$checksumLines = foreach ($archive in $archives) {
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    "$($hash.Hash)  $([IO.Path]::GetFileName($archive))"
}
[IO.File]::WriteAllLines(
    (Join-Path $releaseRoot 'SHA256SUMS.txt'),
    $checksumLines,
    [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $utilityStaging, $mcpStaging -Recurse -Force
Write-Host "Release artifacts: $releaseRoot"
