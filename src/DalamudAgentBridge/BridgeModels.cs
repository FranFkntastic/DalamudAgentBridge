using System.Text.Json;

namespace DalamudAgentBridge;

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
    public required string PluginInternalName { get; init; }
    public string? RuntimeInstanceId { get; init; }
    public string? ProfileId { get; init; }
    public string? ProfileAlias { get; init; }
    public int ProtocolVersion { get; init; } = 1;
}

public sealed record BridgeInstanceView(
    string Id,
    string PluginName,
    string PipeName,
    int ProcessId,
    int SchemaVersion,
    string PluginInstanceId,
    string PluginInternalName,
    string? RuntimeInstanceId,
    string? ProfileId,
    string? ProfileAlias,
    int ProtocolVersion);

public sealed record BridgeTargetSelector(
    string Plugin,
    string? Profile = null,
    int? ProcessId = null);

public sealed record BridgeCommandRequest
{
    public string? Target { get; init; }
    public long? FrameId { get; init; }
    public string? Challenge { get; init; }
    public string? ProofId { get; init; }
    public bool FullViewport { get; init; }
    public string? TransactionId { get; init; }
    public JsonElement? Arguments { get; init; }
    public string? OperationId { get; init; }
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
    public string Scope { get; init; } = string.Empty;
    public string? CaptureMethod { get; init; }
    public string? TargetPlugin { get; init; }
    public string? TransactionId { get; init; }
    public long? FrameId { get; init; }
    public uint? ViewportId { get; init; }
}

public sealed record PluginCaptureReviewReceipt(
    BridgeInstanceView Instance,
    BridgeCaptureReceipt Receipt,
    ReviewCapture Review,
    string ImagePath);

public sealed record PluginSurfaceCaptureReviewReceipt(
    Franthropy.Dalamud.AgentBridge.AgentBridgePluginSurfacePresentationReceipt Presentation,
    PluginCaptureReviewReceipt Capture,
    Franthropy.Dalamud.AgentBridge.AgentBridgePluginSurfacePresentationResult Restoration);

public sealed record PluginBridgeRequest
{
    public required string Token { get; init; }
    public required string Command { get; init; }
    public string? Target { get; init; }
    public long? FrameId { get; init; }
    public string? Challenge { get; init; }
    public string? ProofId { get; init; }
    public bool FullViewport { get; init; }
    public string? TransactionId { get; init; }
    public JsonElement? Arguments { get; init; }
    public string? OperationId { get; init; }
}

public sealed record BridgeCaptureTransactionReceipt(
    string TransactionId,
    string Target,
    long FrameId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReadyAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record ReviewedControlPresentationRequest
{
    public string SurfaceId { get; init; } = string.Empty;
    public IReadOnlyList<string> ControlIds { get; init; } = [];
    public int? TimeoutMilliseconds { get; init; }
}

public sealed record ReviewedControlPresentationReceipt(
    string SurfaceId,
    string SurfaceLabel,
    long FrameId,
    DateTimeOffset RenderedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<Franthropy.Dalamud.AgentBridge.AgentBridgeUiControl> Controls);

public sealed record ReviewedControlActionRequest
{
    public string? SurfaceId { get; init; }
    public string ControlId { get; init; } = string.Empty;
    public int? TimeoutMilliseconds { get; init; }
    public JsonElement? Arguments { get; init; }
    public bool WaitForCompletion { get; init; } = true;
    public int? CompletionTimeoutMilliseconds { get; init; }
    public BridgeWaitCondition? CompletionCondition { get; init; }
}

public sealed record ReviewedControlActionReceipt(
    ReviewedControlPresentationReceipt Presentation,
    PluginBridgeResponse Invocation);

public sealed record PluginBridgeResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public JsonElement? Receipt { get; init; }
    public string? OperationId { get; init; }
}

public sealed record BridgeHealthReceipt(
    BridgeInstanceView Instance,
    bool Reachable,
    string Message,
    Franthropy.Dalamud.AgentBridge.AgentBridgeManifest? Manifest,
    DateTimeOffset CheckedAtUtc);

public sealed record BridgeWaitCondition(
    string Path,
    string? ExpectedValue = null,
    bool? Exists = null);

public sealed record BridgeWaitReceipt(
    string BridgeId,
    BridgeWaitCondition Condition,
    JsonElement Snapshot,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int Attempts);

public sealed record BridgeWaitRequest(
    BridgeWaitCondition Condition,
    int? TimeoutMilliseconds = null);

public sealed record BridgeActionWorkflowReceipt(
    BridgeInstanceView Instance,
    ReviewedControlActionReceipt Action,
    Franthropy.Dalamud.AgentBridge.AgentBridgeOperationSnapshot? Operation,
    JsonElement FinalSnapshot,
    DalamudLogRead Logs,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public sealed record DevPluginDeploymentRequest
{
    public string SourceDirectory { get; init; } = string.Empty;
    public string? ExpectedMainDllSha256 { get; init; }
    public int? TimeoutMilliseconds { get; init; }
}

public sealed record DevPluginDeploymentReceipt(
    BridgeInstanceView Before,
    BridgeInstanceView After,
    string SourceDirectory,
    string TargetDirectory,
    string PreviousMainDllSha256,
    string InstalledMainDllSha256,
    string LoadedMainDllSha256,
    string PreviousRuntimeInstanceId,
    string LoadedRuntimeInstanceId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Reloaded = true);

public sealed record InstalledPluginSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public IReadOnlyList<InstalledPluginState> Plugins { get; init; } = [];
}

public sealed record InstalledPluginState
{
    public string InternalName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool IsLoaded { get; init; }
    public bool IsDev { get; init; }
    public bool IsTesting { get; init; }
    public bool IsThirdParty { get; init; }
    public bool IsOutdated { get; init; }
    public bool IsBanned { get; init; }
    public bool IsOrphaned { get; init; }
    public bool IsDecommissioned { get; init; }
    public bool HasMainUi { get; init; }
    public bool HasConfigUi { get; init; }
}

public sealed record LocalPluginBuildReplacementRequest
{
    public string SourceDirectory { get; init; } = string.Empty;
    public string? ExpectedCurrentVersion { get; init; }
    public string? ExpectedMainDllSha256 { get; init; }
    public bool EnableAfterReplacement { get; init; } = true;
    public bool PreserveInstalledManifest { get; init; } = true;
}

public sealed record LocalPluginBuildReplacementReceipt(
    string InternalName,
    string Version,
    string SourceDirectory,
    string InstalledDirectory,
    string PreviousMainDllSha256,
    string InstalledMainDllSha256,
    bool WasLoaded,
    bool IsLoaded,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);
