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
    bool CanCancel);

public sealed record NavigationSubmissionResult(
    bool Success,
    string Code,
    string Message,
    NavigationSnapshot Navigation);

/// <summary>Owns one explicit vnavmesh movement request and turns it into an observable operation.</summary>
public sealed class NavigationCoordinator : IDisposable
{
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
    private readonly DalamudVNavmeshTravel vnavmesh;
    private ActiveNavigation? active;
    private NavigationSnapshot? last;
    private bool permissionRevocationRequested;

    public NavigationCoordinator(
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        DalamudVNavmeshTravel vnavmesh)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.condition = condition;
        this.vnavmesh = vnavmesh;
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

        var unsafeFlags = new List<string>();
        foreach (var flag in UnsafeConditions)
            if (condition[flag])
                unsafeFlags.Add(flag.ToString());
        if (unsafeFlags.Count > 0)
            return Reject("UnsafeClientState", $"Navigation is unavailable while these conditions are active: {string.Join(", ", unsafeFlags)}.");

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

        var now = DateTimeOffset.UtcNow;
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
            null, null, null, null, null, null, null, DateTimeOffset.UtcNow, null, null,
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
        if (!vnavmesh.TryStop())
            return Reject("CancelFailed", "vnavmesh did not accept the stop request.");

        var current = active;
        var distance = CurrentDistance(current);
        last = Snapshot(current, AgentBridgeOperationState.Cancelled, "Cancelled", "Navigation was cancelled.", distance);
        active = null;
        return new NavigationSubmissionResult(true, "Cancelled", "Navigation was cancelled.", last);
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
        if (permissionRevocationRequested)
        {
            if (vnavmesh.TryStop())
            {
                last = Snapshot(current, AgentBridgeOperationState.Cancelled, "PermissionRevoked", "Navigation stopped because the in-game permission was disabled.", CurrentDistance(current));
                active = null;
                permissionRevocationRequested = false;
            }
            else
            {
                last = Snapshot(current, AgentBridgeOperationState.Running, "PermissionRevocationPending", "Navigation permission is disabled; DAB is retrying the vnavmesh stop request.", CurrentDistance(current));
            }
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

        var now = DateTimeOffset.UtcNow;
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
        if (stop)
            vnavmesh.TryStop();
        last = Snapshot(current, state, code, message, distance);
        active = null;
        permissionRevocationRequested = false;
    }

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
        var now = DateTimeOffset.UtcNow;
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
        VNavmeshLifecycleObservation? lifecycle = null) =>
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
            Math.Max(0d, (DateTimeOffset.UtcNow - value.LastProgressAtUtc).TotalSeconds),
            value.StartedAtUtc,
            DateTimeOffset.UtcNow,
            value.DeadlineUtc,
            value.LastProgressAtUtc,
            lifecycle ?? vnavmesh.Observe(),
            state == AgentBridgeOperationState.Running);

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
    }
}
