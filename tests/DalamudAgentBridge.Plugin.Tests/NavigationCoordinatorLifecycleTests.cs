using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DalamudAgentBridge.Plugin;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Travel;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class NavigationCoordinatorLifecycleTests
{
    [Fact]
    public void ArrivalStopsThenCompletesAfterVnavmeshIsIdle()
    {
        using var fixture = new Fixture();
        fixture.Begin();

        fixture.PlayerPosition = fixture.Destination;
        var stopping = fixture.Coordinator.Observe();
        fixture.Vnavmesh.IsRunning = false;
        var terminal = fixture.Coordinator.Observe();

        Assert.Equal("Stopping", stopping.Code);
        Assert.Equal(AgentBridgeOperationState.Running, stopping.State);
        Assert.Equal(1, fixture.Vnavmesh.StopAttempts);
        Assert.Equal(AgentBridgeOperationState.Succeeded, terminal.State);
        Assert.Equal("Arrived", terminal.Code);
    }

    [Fact]
    public void RetriesFailedStopAndCompletesOnlyAfterVnavmeshIsIdle()
    {
        using var fixture = new Fixture(false, true);
        fixture.Begin();

        fixture.PlayerPosition = fixture.Destination;
        fixture.Coordinator.Observe();
        fixture.Advance(TimeSpan.FromMilliseconds(250));
        fixture.Coordinator.Observe();
        fixture.Vnavmesh.IsRunning = false;
        var terminal = fixture.Coordinator.Observe();

        Assert.Equal(2, fixture.Vnavmesh.StopAttempts);
        Assert.Equal(AgentBridgeOperationState.Succeeded, terminal.State);
        Assert.Equal("Arrived", terminal.Code);
    }

    [Fact]
    public void RetainsContestedOwnershipWhenStopNeverSucceeds()
    {
        using var fixture = new Fixture(false, false, false, false, false);
        fixture.Begin();

        fixture.PlayerPosition = fixture.Destination;
        fixture.Coordinator.Observe();
        for (var attempt = 1; attempt < 5; attempt++)
        {
            fixture.Advance(TimeSpan.FromSeconds(1));
            fixture.Coordinator.Observe();
        }
        var unresolved = fixture.Coordinator.Observe();

        var competing = fixture.Coordinator.TryBegin(fixture.Request, permissionEnabled: true);

        Assert.Equal(5, fixture.Vnavmesh.StopAttempts);
        Assert.Equal(AgentBridgeOperationState.Failed, unresolved.State);
        Assert.Equal("StopUnresolved", unresolved.Code);
        Assert.True(unresolved.OwnershipContested);
        Assert.False(unresolved.CanCancel);
        Assert.False(competing.Success);
        Assert.Equal("NavigationAlreadyRunning", competing.Code);
    }

    [Fact]
    public void SafetyTripMidNavigationStopsBeforeReportingFailure()
    {
        using var fixture = new Fixture();
        fixture.Begin();
        fixture.Unsafe = true;

        var stopping = fixture.Coordinator.Observe();
        fixture.Vnavmesh.IsRunning = false;
        var terminal = fixture.Coordinator.Observe();

        Assert.Equal("Stopping", stopping.Code);
        Assert.Equal(AgentBridgeOperationState.Failed, terminal.State);
        Assert.Equal("UnsafeClientState", terminal.Code);
    }

    [Fact]
    public void TimeoutStopsBeforeReportingFailure()
    {
        using var fixture = new Fixture();
        fixture.Begin(timeoutSeconds: 1);
        fixture.Advance(TimeSpan.FromSeconds(1));

        fixture.Coordinator.Observe();
        fixture.Vnavmesh.IsRunning = false;
        var terminal = fixture.Coordinator.Observe();

        Assert.Equal(AgentBridgeOperationState.Failed, terminal.State);
        Assert.Equal("TimedOut", terminal.Code);
    }

    [Fact]
    public void CancellationStopsBeforeReportingTerminalReceipt()
    {
        using var fixture = new Fixture();
        fixture.Begin();

        var requested = fixture.Coordinator.TryCancel(null);
        fixture.Vnavmesh.IsRunning = false;
        var terminal = fixture.Coordinator.Observe();

        Assert.True(requested.Success);
        Assert.Equal("CancellationRequested", requested.Code);
        Assert.Equal(AgentBridgeOperationState.Cancelled, terminal.State);
        Assert.Equal("Cancelled", terminal.Code);
    }

    [Fact]
    public void PermissionRevocationStopsBeforeReportingTerminalReceipt()
    {
        using var fixture = new Fixture();
        fixture.Begin();

        fixture.Coordinator.RequestPermissionRevocation();
        fixture.Vnavmesh.IsRunning = false;
        var terminal = fixture.Coordinator.Observe();

        Assert.Equal(AgentBridgeOperationState.Cancelled, terminal.State);
        Assert.Equal("PermissionRevoked", terminal.Code);
    }

    private sealed class Fixture : IDisposable
    {
        private DateTimeOffset now = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
        private Vector3 playerPosition = Vector3.Zero;

        public Fixture(params bool[] stopResults)
        {
            Vnavmesh = new FakeNavigationTravel(stopResults);
            var player = Proxy<IGameObject>(call => call.Name == "get_Position" ? playerPosition : null);
            var framework = Proxy<IFramework>(_ => null);
            var clientState = Proxy<IClientState>(call => call.Name == "get_TerritoryType" ? 129u : null);
            var objectTable = Proxy<IObjectTable>(call => call.Name == "get_Item" ? player : null);
            var condition = Proxy<ICondition>(call => call.Name == "get_Item" && Unsafe ? true : false);
            Coordinator = new NavigationCoordinator(framework, clientState, objectTable, condition, Vnavmesh, () => now);
        }

        public NavigationCoordinator Coordinator { get; }
        public FakeNavigationTravel Vnavmesh { get; }
        public bool Unsafe { get; set; }
        public Vector3 PlayerPosition { set => playerPosition = value; }
        public Vector3 Destination => new(Request.X, Request.Y, Request.Z);
        public NavigationPointRequest Request { get; private set; } = new(129, 10, 0, 0, 1, 60);

        public void Begin(int timeoutSeconds = 60)
        {
            Request = Request with { TimeoutSeconds = timeoutSeconds };
            Assert.True(Coordinator.TryBegin(Request, permissionEnabled: true).Success);
        }

        public void Advance(TimeSpan duration) => now = now.Add(duration);
        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class FakeNavigationTravel(params bool[] stopResults) : INavigationTravel
    {
        private readonly Queue<bool> stopResults = new(stopResults);

        public bool IsRunning { get; set; } = true;
        public int StopAttempts { get; private set; }

        public VNavmeshLifecycleObservation Observe() => IsRunning
            ? new(VNavmeshLifecycleState.Running, "PathRunning", "vnavmesh is following a path.")
            : new(VNavmeshLifecycleState.Ready, "Ready", "vnavmesh is ready.");

        public VNavmeshPathSubmissionResult TryMoveCloseTo(Vector3 destination, float range) =>
            new(VNavmeshPathSubmissionState.Submitted, "Submitted", "vnavmesh accepted the path.");

        public bool TryStop()
        {
            StopAttempts++;
            return stopResults.Count == 0 || stopResults.Dequeue();
        }
    }

    private class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler!(targetMethod!);
    }

    private static T Proxy<T>(Func<MethodInfo, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
