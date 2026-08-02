param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$FranthropyDalamudProject
)

$ErrorActionPreference = 'Stop'
$pluginProject = Join-Path $PSScriptRoot 'src\DalamudAgentBridge.Plugin\DalamudAgentBridge.Plugin.csproj'
$pluginOutput = Join-Path $PSScriptRoot "src\DalamudAgentBridge.Plugin\bin\$Configuration"
$repositoryRoot = Join-Path $PSScriptRoot 'src\DalamudAgentBridge\wwwroot\repository'
$stagingRoot = Join-Path $PSScriptRoot '.run\plugin-package'
$zipPath = Join-Path $repositoryRoot 'DalamudAgentBridge.zip'
$repoPath = Join-Path $repositoryRoot 'repo.json'

Remove-Item -LiteralPath $pluginOutput -Recurse -Force -ErrorAction SilentlyContinue
$buildArguments = @('build', $pluginProject, '-c', $Configuration)
if ($FranthropyDalamudProject) {
    $buildArguments += "-p:FranthropyDalamudProject=$FranthropyDalamudProject"
}
dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stagingRoot, $repositoryRoot | Out-Null
Get-ChildItem -LiteralPath $pluginOutput -File |
    Where-Object { $_.Extension -notin @('.pdb', '.xml') } |
    Copy-Item -Destination $stagingRoot
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

$manifest = Get-Content -Raw (Join-Path $pluginOutput 'DalamudAgentBridge.json') | ConvertFrom-Json
$entry = [ordered]@{
    Author = $manifest.Author
    Name = $manifest.Name
    InternalName = $manifest.InternalName
    AssemblyVersion = $manifest.AssemblyVersion
    Description = $manifest.Description
    ApplicableVersion = $manifest.ApplicableVersion
    RepoUrl = 'https://github.com/FranFkntastic/DalamudAgentBridge'
    DalamudApiLevel = $manifest.DalamudApiLevel
    Punchline = $manifest.Punchline
    Tags = $manifest.Tags
    CategoryTags = $manifest.CategoryTags
    IsHide = $false
    IsTestingExclusive = $false
    DownloadCount = 0
    DownloadLinkInstall = 'http://127.0.0.1:45831/repository/DalamudAgentBridge.zip'
    DownloadLinkTesting = 'http://127.0.0.1:45831/repository/DalamudAgentBridge.zip'
    DownloadLinkUpdate = 'http://127.0.0.1:45831/repository/DalamudAgentBridge.zip'
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
}
[System.IO.File]::WriteAllText($repoPath, (@($entry) | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
Write-Host "Repository manifest: $repoPath"
Write-Host "Plugin package: $zipPath"
