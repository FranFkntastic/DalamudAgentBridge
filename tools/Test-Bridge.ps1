[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$ForceVerify,

    [switch]$DisableSharedCompilation
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$developmentRoot = Split-Path -Parent $repoRoot
$franthropyRoot = Join-Path $developmentRoot 'Franthropy\src\Franthropy.Dalamud'
$testProject = Join-Path $repoRoot 'tests\DalamudAgentBridge.Tests\DalamudAgentBridge.Tests.csproj'
$artifacts = Join-Path $repoRoot 'artifacts'
$cachePath = Join-Path $artifacts "bridge-verification-$Configuration.json"
$logPath = Join-Path $artifacts "bridge-verification-$Configuration.log"

function Get-SourceSignature {
    $files = foreach ($root in @((Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests'), $franthropyRoot)) {
        Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
            ($_.Extension -in @('.cs', '.csproj', '.props', '.targets', '.json'))
        }
    }
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $version = (& dotnet --version).Trim()
        $hash.AppendData([Text.Encoding]::UTF8.GetBytes("dotnet=$version;sharedCompilation=$(-not $DisableSharedCompilation)`n"))
        foreach ($file in @($files | Sort-Object FullName -Unique)) {
            $hash.AppendData([Text.Encoding]::UTF8.GetBytes("$($file.FullName.ToLowerInvariant())`n$($file.Length)`n"))
            $stream = [IO.File]::OpenRead($file.FullName)
            try {
                $buffer = [byte[]]::new(65536)
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $hash.AppendData($buffer, 0, $read)
                }
            } finally { $stream.Dispose() }
        }
        [BitConverter]::ToString($hash.GetHashAndReset()).Replace('-', '')
    } finally { $hash.Dispose() }
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$signature = Get-SourceSignature
$cache = if (Test-Path -LiteralPath $cachePath) {
    try { Get-Content -LiteralPath $cachePath -Raw | ConvertFrom-Json } catch { $null }
} else { $null }
$cachedAssembly = $cache.TestAssembly
$cacheValid = -not $ForceVerify -and $null -ne $cache -and $cache.Signature -eq $signature -and
    -not [string]::IsNullOrWhiteSpace($cachedAssembly) -and (Test-Path -LiteralPath $cachedAssembly) -and
    (Get-FileHash -LiteralPath $cachedAssembly -Algorithm SHA256).Hash -eq $cache.TestAssemblyHash
if ($cacheValid) {
    Write-Host 'Bridge source and verified test output are unchanged; reusing the successful verification.'
    return
}

& dotnet test $testProject -c $Configuration --no-restore -p:UseSharedCompilation=$(-not $DisableSharedCompilation) *> $logPath
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $logPath -Tail 160
    throw "Bridge verification failed. Full output: $logPath"
}
$summary = Get-Content -LiteralPath $logPath | Where-Object { $_ -match 'Passed!|Failed!|Build succeeded' } | Select-Object -Last 2
$summary | Write-Host
$assembly = Get-ChildItem -LiteralPath (Join-Path $repoRoot "tests\DalamudAgentBridge.Tests\bin\x64\$Configuration") `
    -Recurse -Filter 'DalamudAgentBridge.Tests.dll' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -eq $assembly) { throw 'The verified bridge test assembly was not found.' }
[ordered]@{
    Signature = $signature
    TestAssembly = $assembly.FullName
    TestAssemblyHash = (Get-FileHash -LiteralPath $assembly.FullName -Algorithm SHA256).Hash
    VerifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content -LiteralPath $cachePath -Encoding utf8
