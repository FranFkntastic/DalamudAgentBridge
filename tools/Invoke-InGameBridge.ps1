[CmdletBinding()]
param(
    [ValidateSet('hello', 'get-snapshot', 'get-login-ui', 'begin-login')]
    [string]$Command = 'hello',
    [string]$Target,
    [int]$ProcessId,
    [string]$PluginConfigRoot = (Join-Path $env:APPDATA 'XIVLauncher\pluginConfigs')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$configPath = Join-Path $PluginConfigRoot 'DalamudAgentBridge.json'
$discoveryDirectory = Join-Path $PluginConfigRoot 'DalamudAgentBridge\agent-bridge'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$discoveries = @(Get-ChildItem -LiteralPath $discoveryDirectory -Filter 'discovery-*.json' -File -ErrorAction SilentlyContinue |
    ForEach-Object {
        try { $value = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json } catch { return }
        if ($null -ne (Get-Process -Id ([int]$value.processId) -ErrorAction SilentlyContinue) -and
            ($ProcessId -eq 0 -or [int]$value.processId -eq $ProcessId)) {
            [pscustomobject]@{ Value = $value; Updated = $_.LastWriteTimeUtc }
        }
    } | Sort-Object Updated -Descending)
if ($discoveries.Count -ne 1) {
    throw "Expected exactly one live standalone bridge discovery; found $($discoveries.Count). Supply -ProcessId when multiple clients share the profile root."
}

$protectedToken = $null
$tokenBytes = $null
try {
    $entropy = [Text.Encoding]::UTF8.GetBytes([string]$config.PluginInstanceId)
    $protectedToken = [Convert]::FromBase64String([string]$config.AgentBridgeProtectedAccessToken)
    $tokenBytes = [Security.Cryptography.ProtectedData]::Unprotect(
        $protectedToken,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $request = @{ token = [Text.Encoding]::UTF8.GetString($tokenBytes); command = $Command; fullViewport = $false }
    if (-not [string]::IsNullOrWhiteSpace($Target)) { $request.target = $Target }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        [string]$discoveries[0].Value.pipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::None,
        [Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(5000)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $reader = [IO.StreamReader]::new($pipe)
        $writer.WriteLine(($request | ConvertTo-Json -Compress))
        $reader.ReadLine()
    }
    finally { $pipe.Dispose() }
}
finally {
    if ($null -ne $tokenBytes) { [Array]::Clear($tokenBytes, 0, $tokenBytes.Length) }
    if ($null -ne $protectedToken) { [Array]::Clear($protectedToken, 0, $protectedToken.Length) }
}
