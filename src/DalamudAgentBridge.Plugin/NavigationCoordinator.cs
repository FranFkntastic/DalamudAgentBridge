using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Travel;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace DalamudAgentBridge.Plugin;

public sealed record NavigationDestination(
    uint TerritoryType,
    float X,
    float Y,
    float Z,
    float ArrivalRadius);

public sealed record NavigationSnapshot(
    string? OperationId,
    AgentBridgeOperationState State,
    string Code,
    string Message,
    NavigationDestination? Destination,
    float? StartDistance,
    float? DistanceRemaining,
    float? BestDistance,
    float? ProgressYalms,
    double? SecondsSinceProgress,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? DeadlineUtc,
    DateTimeOffset? LastProgressAtUtc,
    VNavmeshLifecycleObservation VNavmesh,
    bool CanCancel,
    bool OwnershipContested = false);

public sealed record NavigationSubmissionResult(
    bool Success,
    string Code,
    string Message,
    NavigationSnapshot Navigation);

public interface INavigationTravel
{
    VNavmeshLifecycleObservation Observe();
    VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range);
    bool TryStop();
}

/// <summary>Owns one explicit vnavmesh movement request and turns it into an observable operation.</summary>
public sealed class NavigationCoordinator : IDisposable
{
    private const int MaximumStopAttempts = 5;
    private static readonly TimeSpan InitialStopRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumStopRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly ConditionFlag[] UnsafeConditions =
    [
        ConditionFlag.Unconscious,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.MeldingMateria,
        ConditionFlag.Performing,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent,
        ConditionFlag.TradeOpen,
        ConditionFlag.Fishing,
        ConditionFlag.BetweenAreas,
        ConditionFlag.OccupiedSummoningBell,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.LoggingOut,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
    ];

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly INavigationTravel vnavmesh;
    private readonly Func<DateTimeOffset> utcNow;
    private ActiveNavigation? active;
    private NavigationSnapshot? last;
    private bool permissionRevocationRequested;

    public NavigationCoordinator(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        DalamudVNavmeshTravel vnavmesh)
        : this(framework, clientState, objectTable, condition, new VnavmeshNavigationTravel(vnavmesh), () => DateTimeOffset.UtcNow)
    {
    }

    internal NavigationCoordinator(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        INavigationTravel vnavmesh,
        Func<DateTimeOffset> utcNow)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.vnavmesh = vnavmesh;
        this.utcNow = utcNow;
        framework.Update += OnFrameworkUpdate;
    }

    public NavigationSubmissionResult TryBegin(NavigationPointRequest request, bool permissionEnabled)
    {
        if (!permissionEnabled)
            return Reject("NavigationDisabled", "Agent navigation is disabled in the in-game plugin settings.");
        if (active is not null)
            return Reject("NavigationAlreadyRunning", "DAB already owns an active navigation request.");
        var player = objectTable[0];
        if (player is null)
            return Reject("PlayerUnavailable", "The local player is unavailable.");
        if (clientState.TerritoryType != request.TerritoryType)
            return Reject("TerritoryMismatch", $"The current territory is {clientState.TerritoryType}, not {request.TerritoryType}.");

        if (UnsafeStateMessage() is { } unsafeStateMessage)
            return Reject("UnsafeClientState", unsafeStateMessage);

        var destination = new Vector3(request.X, request.Y, request.Z);
        var distance = Vector3.Distance(player.Position, destination);
        if (distance <= request.ArrivalRadius)
        {
            last = CreateTerminal(
                Guid.NewGuid().ToString("N"), request, AgentBridgeOperationState.Succeeded,
                "AlreadyAtDestination", "The player is already within the arrival radius.", distance, distance);
            return new NavigationSubmissionResult(true, last.Code, last.Message, last);
        }

        var submission = vnavmesh.TryMoveCloseTo(destination, request.ArrivalRadius);
        if (!submission.Submitted)
            return Reject(submission.Code, submission.Message);

        var now = utcNow();
        active = new ActiveNavigation(
            Guid.NewGuid().ToString("N"), request, destination, now, now.AddSeconds(request.TimeoutSeconds),
            distance, distance, now);
        permissionRevocationRequested = false;
        last = Snapshot(active, AgentBridgeOperationState.Running, "PathRunning", "vnavmesh accepted the destination and DAB is observing progress.", distance);
        return new NavigationSubmissionResult(true, "Submitted", "Navigation started.", last);
    }

    public NavigationSnapshot Observe()
    {
        Update();
        return last ?? new NavigationSnapshot(
            null,
            AgentBridgeOperationState.Succeeded,
            "Idle",
            "DAB does not own a navigation request.",
            null, null, null, null, null, null, null, utcNow(), null, null,
            vnavmesh.Observe(),
            false);
    }

    public NavigationSubmissionResult TryCancel(string? operationId)
    {
        Update();
        if (active is null)
        {
            if (!string.IsNullOrWhiteSpace(operationId) && string.Equals(last?.OperationId, operationId, StringComparison.Ordinal))
                return new NavigationSubmissionResult(true, "AlreadyTerminal", "The requested navigation operation is already terminal.", last!);
            return Reject("NoActiveNavigation", "DAB does not own an active navigation request.");
        }
        if (!string.IsNullOrWhiteSpace(operationId) && !string.Equals(active.OperationId, operationId, StringComparison.Ordinal))
            return Reject("OperationMismatch", "The supplied operationId does not identify DAB's active navigation request.");
        var current = active;
        if (current.Stop is null)
            Finish(current, AgentBridgeOperationState.Cancelled, "Cancelled", "Navigation was cancelled.", CurrentDistance(current), stop: true);
        return new NavigationSubmissionResult(true, "CancellationRequested", "DAB is confirming that vnavmesh has stopped.", last!);
    }

    public void RequestPermissionRevocation()
    {
        permissionRevocationRequested = active is not null;
        Update();
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        if (active is not null)
            vnavmesh.TryStop();
        active = null;
    }

    private void OnFrameworkUpdate(IFramework _) => Update();

    private void Update()
    {
        if (active is not { } current)
            return;
        if (current.Stop is not null)
        {
            UpdateStopping(current);
            return;
        }
        if (permissionRevocationRequested)
        {
            Finish(current, AgentBridgeOperationState.Cancelled, "PermissionRevoked", "Navigation stopped because the in-game permission was disabled.", CurrentDistance(current), stop: true);
            return;
        }
        if (UnsafeStateMessage() is { } unsafeStateMessage)
        {
            Finish(current, AgentBridgeOperationState.Failed, "UnsafeClientState", unsafeStateMessage, CurrentDistance(current), stop: true);
            return;
        }
        var player = objectTable[0];
        if (player is null)
        {
            Finish(current, AgentBridgeOperationState.Failed, "PlayerUnavailable", "The local player became unavailable.", null, stop: true);
            return;
        }
        if (clientState.TerritoryType != current.Request.TerritoryType)
        {
            Finish(current, AgentBridgeOperationState.Failed, "TerritoryChanged", "The territory changed before arrival.", null, stop: true);
            return;
        }

        var now = utcNow();
        var distance = Vector3.Distance(player.Position, current.Destination);
        if (distance < current.BestDistance)
        {
            var meaningfulProgress = distance + 0.1f < current.BestDistance;
            current.BestDistance = distance;
            if (meaningfulProgress)
                current.LastProgressAtUtc = now;
        }
        if (distance <= current.Request.ArrivalRadius)
        {
            Finish(current, AgentBridgeOperationState.Succeeded, "Arrived", "The player reached the destination.", distance, stop: true);
            return;
        }
        if (now >= current.DeadlineUtc)
        {
            Finish(current, AgentBridgeOperationState.Failed, "TimedOut", "Navigation exceeded its requested timeout.", distance, stop: true);
            return;
        }

        var lifecycle = vnavmesh.Observe();
        if (now - current.StartedAtUtc >= TimeSpan.FromSeconds(2) && !lifecycle.IsRunning)
        {
            Finish(current, AgentBridgeOperationState.Failed, lifecycle.Code, "vnavmesh stopped before the destination was reached.", distance, stop: false);
            return;
        }
        last = Snapshot(current, AgentBridgeOperationState.Running, lifecycle.Code, lifecycle.Message, distance, lifecycle);
    }

    private void Finish(
        ActiveNavigation current,
        AgentBridgeOperationState state,
        string code,
        string message,
        float? distance,
        bool stop)
    {
        if (!stop)
        {
            Complete(current, state, code, message, distance);
            return;
        }

        current.Stop = new PendingStop(state, code, message, distance, utcNow());
        UpdateStopping(current);
    }

    private void UpdateStopping(ActiveNavigation current)
    {
        var stop = current.Stop!;
        var lifecycle = vnavmesh.Observe();
        if (!lifecycle.IsRunning)
        {
            Complete(current, stop.State, stop.Code, stop.Message, stop.Distance);
            return;
        }

        var now = utcNow();
        if (stop.Attempts >= MaximumStopAttempts)
        {
            last = Snapshot(
                current,
                AgentBridgeOperationState.Failed,
                "StopUnresolved",
                "vnavmesh remained running after DAB exhausted its stop retry budget; ownership remains contested.",
                CurrentDistance(current),
                lifecycle,
                ownershipContested: true);
            return;
        }

        if (now >= stop.NextAttemptAtUtc)
        {
            stop.Attempts++;
            var accepted = vnavmesh.TryStop();
            stop.NextAttemptAtUtc = now.Add(StopRetryDelay(stop.Attempts));
            lifecycle = vnavmesh.Observe();
            if (!lifecycle.IsRunning)
            {
                Complete(current, stop.State, stop.Code, stop.Message, stop.Distance);
                return;
            }

            last = Snapshot(
                current,
                AgentBridgeOperationState.Running,
                "Stopping",
                accepted
                    ? "DAB requested a vnavmesh stop and is waiting for its running state to clear."
                    : "vnavmesh did not accept DAB's stop request; DAB is retaining ownership and will retry.",
                CurrentDistance(current),
                lifecycle);
            return;
        }

        last = Snapshot(
            current,
            AgentBridgeOperationState.Running,
            "Stopping",
            "DAB is retaining navigation ownership while waiting to retry the vnavmesh stop request.",
            CurrentDistance(current),
            lifecycle);
    }

    private void Complete(ActiveNavigation current, AgentBridgeOperationState state, string code, string message, float? distance)
    {
        last = Snapshot(current, state, code, message, distance);
        active = null;
        permissionRevocationRequested = false;
    }

    private string? UnsafeStateMessage()
    {
        var unsafeFlags = new List<string>();
        foreach (var flag in UnsafeConditions)
            if (condition[flag])
                unsafeFlags.Add(flag.ToString());
        return unsafeFlags.Count == 0
            ? null
            : $"Navigation is unavailable while these conditions are active: {string.Join(", ", unsafeFlags)}.";
    }

    private static TimeSpan StopRetryDelay(int attempts) => TimeSpan.FromMilliseconds(Math.Min(
        InitialStopRetryDelay.TotalMilliseconds * Math.Pow(2, attempts - 1),
        MaximumStopRetryDelay.TotalMilliseconds));

    private float? CurrentDistance(ActiveNavigation current)
    {
        var player = objectTable[0];
        return player is null ? null : Vector3.Distance(player.Position, current.Destination);
    }

    private NavigationSubmissionResult Reject(string code, string message) =>
        new(false, code, message, Observe());

    private NavigationSnapshot CreateTerminal(
        string operationId,
        NavigationPointRequest request,
        AgentBridgeOperationState state,
        string code,
        string message,
        float distance,
        float bestDistance)
    {
        var now = utcNow();
        return new NavigationSnapshot(
            operationId, state, code, message, Destination(request), distance, distance, bestDistance, 0f, 0d,
            now, now, now, now, vnavmesh.Observe(), false);
    }

    private NavigationSnapshot Snapshot(
        ActiveNavigation value,
        AgentBridgeOperationState state,
        string code,
        string message,
        float? distance,
        VNavmeshLifecycleObservation? lifecycle = null,
        bool ownershipContested = false) =>
        new(
            value.OperationId,
            state,
            code,
            message,
            Destination(value.Request),
            value.StartDistance,
            distance,
            value.BestDistance,
            value.StartDistance - value.BestDistance,
            Math.Max(0d, (utcNow() - value.LastProgressAtUtc).TotalSeconds),
            value.StartedAtUtc,
            utcNow(),
            value.DeadlineUtc,
            value.LastProgressAtUtc,
            lifecycle ?? vnavmesh.Observe(),
            state == AgentBridgeOperationState.Running && !ownershipContested,
            ownershipContested);

    private static NavigationDestination Destination(NavigationPointRequest request) =>
        new(request.TerritoryType, request.X, request.Y, request.Z, request.ArrivalRadius);

    private sealed class ActiveNavigation(
        string operationId,
        NavigationPointRequest request,
        Vector3 destination,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc,
        float startDistance,
        float bestDistance,
        DateTimeOffset lastProgressAtUtc)
    {
        public string OperationId { get; } = operationId;
        public NavigationPointRequest Request { get; } = request;
        public Vector3 Destination { get; } = destination;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public float StartDistance { get; } = startDistance;
        public float BestDistance { get; set; } = bestDistance;
        public DateTimeOffset LastProgressAtUtc { get; set; } = lastProgressAtUtc;
        public PendingStop? Stop { get; set; }
    }

    private sealed class PendingStop(
        AgentBridgeOperationState state,
        string code,
        string message,
        float? distance,
        DateTimeOffset requestedAtUtc)
    {
        public AgentBridgeOperationState State { get; } = state;
        public string Code { get; } = code;
        public string Message { get; } = message;
        public float? Distance { get; } = distance;
        public int Attempts { get; set; }
        public DateTimeOffset NextAttemptAtUtc { get; set; } = requestedAtUtc;
    }

    private sealed class VnavmeshNavigationTravel(DalamudVNavmeshTravel vnavmesh) : INavigationTravel
    {
        public VNavmeshLifecycleObservation Observe() => vnavmesh.Observe();
        public VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range) => vnavmesh.TryMoveCloseTo(destination, range);
        public bool TryStop() => vnavmesh.TryStop();
    }
}
