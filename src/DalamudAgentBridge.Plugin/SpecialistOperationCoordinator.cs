using Franthropy.Dalamud.AgentBridge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DalamudAgentBridge.Plugin;

/// <summary>Owns one reviewed specialist objective and projects plugin IPC state into a truthful lifecycle receipt.</summary>
public sealed class SpecialistOperationCoordinator : IDisposable
{
    private static readonly TimeSpan StartupObservationWindow = TimeSpan.FromSeconds(5);
    private readonly IReadOnlyList<ISpecialistAdapter> adapters;
    private readonly IReadOnlyList<SpecialistCapabilityDescriptor> capabilities;
    private readonly GameplayControlLease gameplayLease;
    private readonly Func<DateTimeOffset> utcNow;
    private ActiveSpecialistOperation? active;
    private SpecialistOperationSnapshot? last;
    private bool permissionRevocationRequested;
    private DateTimeOffset nextPollAtUtc;

    public SpecialistOperationCoordinator(
        IReadOnlyList<ISpecialistAdapter> adapters,
        GameplayControlLease gameplayLease,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.adapters = adapters;
        this.gameplayLease = gameplayLease;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        capabilities = adapters.SelectMany(adapter => adapter.Capabilities).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        if (capabilities.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != capabilities.Count)
            throw new InvalidOperationException("Specialist capability ids must be unique.");
    }

    public IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities => capabilities;
    public bool HasActiveOperation => active is not null;

    public SpecialistCatalogSnapshot Observe(bool permissionEnabled)
    {
        Tick(force: true);
        var observations = adapters.Select(SafeObserve).ToArray();
        return new SpecialistCatalogSnapshot(
            1,
            utcNow(),
            permissionEnabled,
            capabilities,
            observations,
            CurrentOperation(),
            gameplayLease.Observe(),
            "ReviewedDalamudIpcAdapters");
    }

    public SpecialistSituationSnapshot ObserveSituation(bool permissionEnabled)
    {
        var catalog = Observe(permissionEnabled);
        return new SpecialistSituationSnapshot(
            permissionEnabled,
            catalog.Plugins.Select(value => new SpecialistPluginSummary(
                value.Plugin,
                value.Version,
                value.Loaded,
                value.Compatible,
                value.Busy,
                value.Code)).ToArray(),
            catalog.Operation,
            catalog.GameplayLease);
    }

    public SpecialistSubmissionResult TryBegin(string capabilityId, JsonElement? arguments, bool permissionEnabled)
    {
        var validation = SpecialistRequestPolicy.Validate(capabilityId, arguments, capabilities);
        if (!validation.Success || validation.Request is null)
            return Reject(validation.Code, validation.Message);
        if (!permissionEnabled)
            return Reject("SpecialistAutomationDisabled", "Specialist automation is disabled in DAB's in-game settings.");

        Tick(force: true);
        if (active is not null)
            return Reject("SpecialistAlreadyRunning", "DAB already owns an active specialist operation.");
        var externallyBusy = adapters.Select(SafeObserve).FirstOrDefault(value => value.Busy);
        if (externallyBusy is not null)
            return Reject("SpecialistExternallyBusy", $"{externallyBusy.Plugin} is already busy outside DAB; its work will not be stolen.");

        var capability = capabilities.Single(value => value.Id == validation.Request.CapabilityId);
        var adapter = adapters.Single(value => string.Equals(value.Plugin, capability.Plugin, StringComparison.Ordinal));
        var readiness = SafeObserve(adapter);
        if (!readiness.Compatible)
            return Reject(readiness.Code, readiness.Message);

        var operationId = Guid.NewGuid().ToString("N");
        if (!gameplayLease.TryAcquire(operationId, "specialist", capability.Id, out var owner))
            return Reject("GameplayControlOwned", $"Gameplay control is owned by {owner.Owner} operation {owner.OperationId}.");

        var started = adapter.TryStart(capability.Id, validation.Request.Parameters);
        if (!started.Accepted)
        {
            gameplayLease.Release(operationId);
            return Reject(started.Code, started.Message);
        }

        var now = utcNow();
        var observation = SafeObserve(adapter);
        active = new ActiveSpecialistOperation(
            operationId,
            capability,
            adapter,
            validation.Request.Parameters.Clone(),
            now,
            now.AddSeconds(validation.Request.TimeoutSeconds),
            observation.Busy);
        last = Snapshot(
            active,
            observation.Busy ? AgentBridgeOperationState.Running : AgentBridgeOperationState.Queued,
            observation.Busy ? "Running" : "AwaitingStartObservation",
            observation.Busy ? observation.Message : $"{adapter.Plugin} accepted the request; DAB is waiting to observe it running.",
            observation);
        return new SpecialistSubmissionResult(true, started.Code, started.Message, last);
    }

    public SpecialistSubmissionResult TryCancel(string? operationId)
    {
        Tick(force: true);
        if (active is null)
        {
            if (!string.IsNullOrWhiteSpace(operationId) && string.Equals(last?.OperationId, operationId, StringComparison.Ordinal))
                return new SpecialistSubmissionResult(true, "AlreadyTerminal", "The requested specialist operation is already terminal.", last!);
            return Reject("NoActiveSpecialist", "DAB does not own an active specialist operation.");
        }
        if (!string.IsNullOrWhiteSpace(operationId) && !string.Equals(active.OperationId, operationId, StringComparison.Ordinal))
            return Reject("OperationMismatch", "The supplied operationId does not identify DAB's active specialist operation.");
        return RequestCancellation(active, "CancellationRequested", "Specialist cancellation was requested.");
    }

    public void RequestPermissionRevocation()
    {
        permissionRevocationRequested = active is not null;
        Tick(force: true);
    }

    public void Tick(bool force = false)
    {
        if (active is not { } current)
            return;
        var now = utcNow();
        if (!force && now < nextPollAtUtc)
            return;
        nextPollAtUtc = now.AddMilliseconds(500);

        if (permissionRevocationRequested && !current.CancellationRequested)
        {
            var result = current.Adapter.TryCancel();
            if (!result.Accepted)
            {
                last = Snapshot(current, AgentBridgeOperationState.Running, result.Code, result.Message, SafeObserve(current.Adapter));
                return;
            }
            current.CancellationRequested = true;
            current.CancellationCode = "PermissionRevoked";
            current.CancellationMessage = "Specialist permission was disabled; DAB requested cancellation and is waiting for the plugin to become idle.";
        }

        var observation = SafeObserve(current.Adapter);
        if (!observation.Compatible)
        {
            Finish(current, AgentBridgeOperationState.Failed, observation.Code, observation.Message, observation);
            return;
        }
        if (current.CancellationRequested)
        {
            if (!observation.Busy)
            {
                Finish(
                    current,
                    current.CancellationState,
                    current.CancellationCode ?? "Cancelled",
                    current.CancellationMessage ?? "Specialist operation was cancelled.",
                    observation);
                return;
            }
            last = Snapshot(current, AgentBridgeOperationState.Running, "CancellationPending", "Cancellation was accepted; DAB is waiting for the plugin to become idle.", observation);
            return;
        }
        if (now >= current.DeadlineUtc)
        {
            var cancellation = current.Adapter.TryCancel();
            current.CancellationRequested = true;
            current.CancellationState = AgentBridgeOperationState.Failed;
            current.CancellationCode = "TimedOut";
            current.CancellationMessage = "The specialist exceeded its deadline and DAB requested cancellation; it will retain gameplay control until the plugin becomes idle.";
            last = Snapshot(
                current,
                AgentBridgeOperationState.Running,
                "CancellationPending",
                cancellation.Accepted
                    ? current.CancellationMessage
                    : $"The specialist exceeded its deadline and cancellation was not accepted: {cancellation.Message} DAB is retaining gameplay control until the plugin becomes idle.",
                observation);
            return;
        }
        if (observation.Busy)
        {
            current.SawBusy = true;
            last = Snapshot(current, AgentBridgeOperationState.Running, observation.Code, observation.Message, observation);
            return;
        }
        if (current.SawBusy)
        {
            Finish(current, AgentBridgeOperationState.Succeeded, "Completed", $"{current.Adapter.Plugin} returned to idle.", observation);
            return;
        }
        if (now - current.StartedAtUtc >= StartupObservationWindow)
        {
            Finish(current, AgentBridgeOperationState.Failed, "StartNotObserved", $"{current.Adapter.Plugin} accepted the request but never reported busy.", observation);
            return;
        }

        last = Snapshot(current, AgentBridgeOperationState.Queued, "AwaitingStartObservation", $"{current.Adapter.Plugin} accepted the request; DAB is waiting to observe it running.", observation);
    }

    public void Dispose()
    {
        if (active is { } current)
        {
            current.Adapter.TryCancel();
            gameplayLease.Release(current.OperationId);
        }
        active = null;
    }

    private SpecialistSubmissionResult RequestCancellation(ActiveSpecialistOperation current, string code, string message)
    {
        var cancellation = current.Adapter.TryCancel();
        if (!cancellation.Accepted)
            return Reject(cancellation.Code, cancellation.Message);
        current.CancellationRequested = true;
        current.CancellationCode = code;
        current.CancellationMessage = message;
        var observation = SafeObserve(current.Adapter);
        if (!observation.Busy)
            Finish(current, AgentBridgeOperationState.Cancelled, code, message, observation);
        else
            last = Snapshot(current, AgentBridgeOperationState.Running, "CancellationPending", "Cancellation was accepted; DAB is waiting for the plugin to become idle.", observation);
        return new SpecialistSubmissionResult(true, code, message, last!);
    }

    private void Finish(
        ActiveSpecialistOperation current,
        AgentBridgeOperationState state,
        string code,
        string message,
        SpecialistPluginObservation observation)
    {
        last = Snapshot(current, state, code, message, observation);
        gameplayLease.Release(current.OperationId);
        active = null;
        permissionRevocationRequested = false;
    }

    private SpecialistPluginObservation SafeObserve(ISpecialistAdapter adapter)
    {
        try
        {
            return adapter.Observe();
        }
        catch (Exception exception)
        {
            return new SpecialistPluginObservation(
                adapter.Plugin,
                null,
                false,
                false,
                false,
                false,
                "AdapterObservationFailed",
                $"{adapter.Plugin} observation failed: {exception.Message}",
                new Dictionary<string, string?>(),
                utcNow());
        }
    }

    private SpecialistOperationSnapshot CurrentOperation() => last ?? Idle();

    private SpecialistOperationSnapshot Idle() => new(
        null,
        AgentBridgeOperationState.Succeeded,
        "Idle",
        "DAB does not own a specialist operation.",
        null,
        null,
        null,
        null,
        utcNow(),
        null,
        null,
        gameplayLease.Observe(),
        false);

    private SpecialistOperationSnapshot Snapshot(
        ActiveSpecialistOperation value,
        AgentBridgeOperationState state,
        string code,
        string message,
        SpecialistPluginObservation observation) =>
        new(
            value.OperationId,
            state,
            code,
            message,
            value.Adapter.Plugin,
            value.Capability.Id,
            value.Parameters,
            value.StartedAtUtc,
            utcNow(),
            value.DeadlineUtc,
            observation,
            gameplayLease.Observe(),
            state is AgentBridgeOperationState.Queued or AgentBridgeOperationState.Running);

    private SpecialistSubmissionResult Reject(string code, string message) =>
        new(false, code, message, CurrentOperation());

    private sealed class ActiveSpecialistOperation(
        string operationId,
        SpecialistCapabilityDescriptor capability,
        ISpecialistAdapter adapter,
        JsonElement parameters,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc,
        bool sawBusy)
    {
        public string OperationId { get; } = operationId;
        public SpecialistCapabilityDescriptor Capability { get; } = capability;
        public ISpecialistAdapter Adapter { get; } = adapter;
        public JsonElement Parameters { get; } = parameters;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public bool SawBusy { get; set; } = sawBusy;
        public bool CancellationRequested { get; set; }
        public AgentBridgeOperationState CancellationState { get; set; } = AgentBridgeOperationState.Cancelled;
        public string? CancellationCode { get; set; }
        public string? CancellationMessage { get; set; }
    }
}
