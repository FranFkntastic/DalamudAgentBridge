using DalamudAgentBridge.Plugin;
using Franthropy.Dalamud.AgentBridge;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class SpecialistOperationCoordinatorTests
{
    [Fact]
    public void PermissionAndExternalWorkFailClosedWithoutInvokingAdapter()
    {
        var fixture = new Fixture();

        var disabled = fixture.Coordinator.TryBegin("test.run", Envelope(), permissionEnabled: false);
        fixture.Adapter.Busy = true;
        var externallyBusy = fixture.Coordinator.TryBegin("test.run", Envelope(), permissionEnabled: true);

        Assert.Equal("SpecialistAutomationDisabled", disabled.Code);
        Assert.Equal("SpecialistExternallyBusy", externallyBusy.Code);
        Assert.Equal(0, fixture.Adapter.StartCalls);
    }

    [Fact]
    public void RunningSpecialistOwnsGameplayUntilObservedIdle()
    {
        var fixture = new Fixture();
        fixture.Adapter.BusyAfterStart = true;

        var started = fixture.Coordinator.TryBegin("test.run", Envelope(), permissionEnabled: true);
        var competingLease = fixture.Lease.TryAcquire("navigation-id", "navigation", "navigate-to", out var owner);
        fixture.Adapter.Busy = false;
        var completed = fixture.Coordinator.Observe(permissionEnabled: true).Operation;

        Assert.True(started.Success);
        Assert.Equal(AgentBridgeOperationState.Running, started.Operation.State);
        Assert.False(competingLease);
        Assert.Equal(started.Operation.OperationId, owner.OperationId);
        Assert.Equal(AgentBridgeOperationState.Succeeded, completed.State);
        Assert.False(fixture.Lease.Observe().IsOwned);
    }

    [Fact]
    public void ExactCancellationWaitsForPluginToBecomeIdle()
    {
        var fixture = new Fixture();
        fixture.Adapter.BusyAfterStart = true;
        var started = fixture.Coordinator.TryBegin("test.run", Envelope(), permissionEnabled: true);

        var mismatch = fixture.Coordinator.TryCancel("wrong-operation");
        var cancellation = fixture.Coordinator.TryCancel(started.Operation.OperationId);
        fixture.Adapter.Busy = false;
        fixture.Coordinator.Tick();
        var terminal = fixture.Coordinator.Observe(permissionEnabled: true).Operation;

        Assert.Equal("OperationMismatch", mismatch.Code);
        Assert.True(cancellation.Success);
        Assert.Equal("CancellationRequested", cancellation.Code);
        Assert.Equal(1, fixture.Adapter.CancelCalls);
        Assert.Equal(AgentBridgeOperationState.Cancelled, terminal.State);
        Assert.False(fixture.Lease.Observe().IsOwned);
    }

    [Fact]
    public void PermissionRevocationAndTimeoutBothRequestCancellation()
    {
        var revoked = new Fixture();
        revoked.Adapter.BusyAfterStart = true;
        revoked.Coordinator.TryBegin("test.run", Envelope(), permissionEnabled: true);
        revoked.Coordinator.RequestPermissionRevocation();
        revoked.Adapter.Busy = false;
        revoked.Coordinator.Tick();

        var timedOut = new Fixture();
        timedOut.Adapter.BusyAfterStart = true;
        timedOut.Coordinator.TryBegin("test.run", Envelope(timeoutSeconds: 15), permissionEnabled: true);
        timedOut.Clock = timedOut.Clock.AddSeconds(16);
        timedOut.Coordinator.Tick();

        Assert.Equal(AgentBridgeOperationState.Cancelled, revoked.Coordinator.Observe(false).Operation.State);
        Assert.Equal("PermissionRevoked", revoked.Coordinator.Observe(false).Operation.Code);
        Assert.Equal(1, revoked.Adapter.CancelCalls);
        Assert.Equal(AgentBridgeOperationState.Failed, timedOut.Coordinator.Observe(true).Operation.State);
        Assert.Equal("TimedOut", timedOut.Coordinator.Observe(true).Operation.Code);
        Assert.Equal(1, timedOut.Adapter.CancelCalls);
    }

    [Fact]
    public void AcceptedButUnobservedStartFailsTruthfully()
    {
        var fixture = new Fixture();
        fixture.Coordinator.TryBegin("test.run", Envelope(), permissionEnabled: true);

        fixture.Clock = fixture.Clock.AddSeconds(6);
        fixture.Coordinator.Tick();
        var result = fixture.Coordinator.Observe(true).Operation;

        Assert.Equal(AgentBridgeOperationState.Failed, result.State);
        Assert.Equal("StartNotObserved", result.Code);
        Assert.False(fixture.Lease.Observe().IsOwned);
    }

    private static JsonElement Envelope(int timeoutSeconds = 60) =>
        JsonSerializer.SerializeToElement(new
        {
            timeoutSeconds,
            parameters = new { value = "work" },
        });

    private sealed class Fixture
    {
        public DateTimeOffset Clock { get; set; } = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        public GameplayControlLease Lease { get; } = new();
        public FakeAdapter Adapter { get; } = new();
        public SpecialistOperationCoordinator Coordinator { get; }

        public Fixture()
        {
            Coordinator = new SpecialistOperationCoordinator([Adapter], Lease, () => Clock);
        }
    }

    private sealed class FakeAdapter : ISpecialistAdapter
    {
        public string Plugin => "TestPlugin";
        public IReadOnlyList<SpecialistCapabilityDescriptor> Capabilities { get; } =
        [
            new(
                "test.run",
                "TestPlugin",
                "Run",
                "Run fake work.",
                "Test",
                60,
                [new("value", SpecialistArgumentKind.String, "Value")]),
        ];

        public bool Busy { get; set; }
        public bool BusyAfterStart { get; set; }
        public int StartCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public SpecialistPluginObservation Observe() => new(
            Plugin,
            "1.0.0",
            true,
            true,
            true,
            Busy,
            Busy ? "Running" : "Idle",
            Busy ? "Fake adapter is running." : "Fake adapter is idle.",
            new Dictionary<string, string?>(),
            DateTimeOffset.UtcNow);

        public SpecialistAdapterStartResult TryStart(string capabilityId, JsonElement parameters)
        {
            StartCalls++;
            Busy = BusyAfterStart;
            return new(true, "Accepted", "Fake adapter accepted work.");
        }

        public SpecialistAdapterCancelResult TryCancel()
        {
            CancelCalls++;
            return new(true, "CancellationRequested", "Fake adapter accepted cancellation.");
        }
    }
}
