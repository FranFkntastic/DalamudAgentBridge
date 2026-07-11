using System.Text.Json;

namespace DalamudAgentBridge;

public sealed record BridgeDiscovery
{
    public int SchemaVersion { get; init; }
    public string PipeName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string PluginInstanceId { get; init; } = string.Empty;
}

public sealed record BridgeInstance
{
    public required string Id { get; init; }
    public required string PluginName { get; init; }
    public required string PipeName { get; init; }
    public required int ProcessId { get; init; }
    public required int SchemaVersion { get; init; }
    public required string PluginInstanceId { get; init; }
    public required string AccessToken { get; init; }
    public required string DiscoveryPath { get; init; }
}

public sealed record BridgeInstanceView(
    string Id,
    string PluginName,
    string PipeName,
    int ProcessId,
    int SchemaVersion,
    string PluginInstanceId);

public sealed record BridgeCommandRequest
{
    public string? Target { get; init; }
    public string? Challenge { get; init; }
    public string? ProofId { get; init; }
}

public sealed record BridgeCaptureReceipt
{
    public int SchemaVersion { get; init; }
    public string CaptureId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public int ProcessId { get; init; }
}

public sealed record PluginBridgeRequest
{
    public required string Token { get; init; }
    public required string Command { get; init; }
    public string? Target { get; init; }
    public string? Challenge { get; init; }
    public string? ProofId { get; init; }
}

public sealed record PluginBridgeResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public JsonElement? Receipt { get; init; }
}
