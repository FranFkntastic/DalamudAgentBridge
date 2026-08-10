using Franthropy.Dalamud.AgentBridge;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DalamudAgentBridge.Plugin;

public enum SpecialistArgumentKind
{
    String,
    UInt32,
    Integer,
    Boolean,
}

public sealed record SpecialistArgumentDescriptor(
    string Name,
    SpecialistArgumentKind Kind,
    string Description,
    bool Required = true,
    string? DefaultValue = null,
    long? Minimum = null,
    long? Maximum = null,
    int MaximumLength = 256);

public sealed record SpecialistCapabilityDescriptor(
    string Id,
    string Plugin,
    string Label,
    string Description,
    string Risk,
    int DefaultTimeoutSeconds,
    IReadOnlyList<SpecialistArgumentDescriptor> Arguments);

public sealed record SpecialistPluginObservation(
    string Plugin,
    string? Version,
    bool Installed,
    bool Loaded,
    bool Compatible,
    bool Busy,
    string Code,
    string Message,
    IReadOnlyDictionary<string, string?> Details,
    DateTimeOffset ObservedAtUtc);

public sealed record SpecialistAdapterStartResult(bool Accepted, string Code, string Message);
public sealed record SpecialistAdapterCancelResult(bool Accepted, string Code, string Message);

public interface ISpecialistAdapter
{
    string Plugin { get; }
    IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; }
    SpecialistPluginObservation Observe();
    SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters);
    SpecialistAdapterCancelResult TryCancel();
}

public sealed record SpecialistOperationSnapshot(
    string? OperationId,
    AgentBridgeOperationState State,
    string Code,
    string Message,
    string? Plugin,
    string? CapabilityId,
    JsonElement? Parameters,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? DeadlineUtc,
    SpecialistPluginObservation? Observation,
    GameplayControlLeaseSnapshot GameplayLease,
    bool CanCancel);

public sealed record SpecialistCatalogSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    bool PermissionEnabled,
    IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities,
    IReadOnlyList<SpecialistPluginObservation> Plugins,
    SpecialistOperationSnapshot Operation,
    GameplayControlLeaseSnapshot GameplayLease,
    string Provenance);

public sealed record SpecialistPluginSummary(
    string Plugin,
    string? Version,
    bool Loaded,
    bool Compatible,
    bool Busy,
    string Code);

public sealed record SpecialistSituationSnapshot(
    bool PermissionEnabled,
    IReadOnlyList<SpecialistPluginSummary> Plugins,
    SpecialistOperationSnapshot Operation,
    GameplayControlLeaseSnapshot GameplayLease);

public sealed record SpecialistSubmissionResult(
    bool Success,
    string Code,
    string Message,
    SpecialistOperationSnapshot Operation);

public sealed record SpecialistStartEnvelope(
    string CapabilityId,
    JsonElement Parameters,
    int TimeoutSeconds);

public sealed record SpecialistRequestValidation(
    bool Success,
    string Code,
    string Message,
    SpecialistStartEnvelope? Request = null);
