using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class ReviewedControlPresentationServiceTests
{
    [Fact]
    public async Task PresentAsync_OpensAdvertisedSurfaceAndReturnsOneSharedReviewedFrame()
    {
        var commands = new List<string>();
        var surfacePresented = false;
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            commands.Add($"{command}:{request?.Target}");
            if (command == "select-main-tab")
                surfacePresented = true;
            object? receipt = command switch
            {
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("squire", "Squire", "select-main-tab", "Squire", 1) },
                "get-control" when surfacePresented => new AgentBridgeUiControlReview(
                    42, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Control", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, "Ready")),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = command != "get-control" || surfacePresented,
                Message = "ok",
                Receipt = receipt == null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });

        var receipt = await service.PresentAsync(CreateInstance(), new ReviewedControlPresentationRequest
        {
            SurfaceId = "squire",
            ControlIds = ["first", "second"],
        }, CancellationToken.None);

        Assert.Equal(42, receipt.FrameId);
        Assert.Equal(["first", "second"], receipt.Controls.Select(control => control.Id));
        Assert.Contains("open-main-window:", commands);
        Assert.Contains("select-main-tab:Squire", commands);
    }

    [Fact]
    public async Task PresentAndInvokeAsync_UsesTheReviewedFrameWithoutAnotherClientRoundTrip()
    {
        var commands = new List<string>();
        var surfacePresented = false;
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            commands.Add($"{command}:{request?.Target}:{request?.FrameId}");
            if (command == "select-main-tab")
                surfacePresented = true;
            object? receipt = command switch
            {
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("squire", "Squire", "select-main-tab", "Squire", 1) },
                "get-control" when surfacePresented => new AgentBridgeUiControlReview(
                    42, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Control", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, "Ready")),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = command != "get-control" || surfacePresented,
                Message = "ok",
                Receipt = receipt == null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });

        var receipt = await service.PresentAndInvokeAsync(CreateInstance(), new ReviewedControlActionRequest
        {
            SurfaceId = "squire",
            ControlId = "squire.refresh",
        }, CancellationToken.None);

        Assert.True(receipt.Invocation.Success);
        Assert.Contains("invoke-control:squire.refresh:42", commands);
    }

    [Fact]
    public async Task PresentAsync_ReusesCurrentSurfaceAndCachedCatalogForRepeatedReview()
    {
        var commands = new List<string>();
        var surfacePresented = false;
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            commands.Add(command);
            if (command == "select-main-tab")
                surfacePresented = true;
            object? receipt = command switch
            {
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("squire", "Squire", "select-main-tab", "Squire", 1) },
                "get-control" when surfacePresented => new AgentBridgeUiControlReview(
                    42, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Control", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, "Ready")),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = command != "get-control" || surfacePresented,
                Message = "ok",
                Receipt = receipt == null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });
        var request = new ReviewedControlPresentationRequest { SurfaceId = "squire", ControlIds = ["squire.refresh"] };

        await service.PresentAsync(CreateInstance(), request, CancellationToken.None);
        await service.PresentAsync(CreateInstance(), request, CancellationToken.None);

        Assert.Equal(1, commands.Count(command => command == "get-review-surfaces"));
        Assert.Equal(1, commands.Count(command => command == "open-main-window"));
        Assert.Equal(1, commands.Count(command => command == "select-main-tab"));
    }

    private static BridgeInstance CreateInstance() => new()
    {
        Id = "MarketMafioso-1",
        PluginName = "MarketMafioso",
        PluginInternalName = "MarketMafioso",
        PipeName = "pipe",
        ProcessId = 1,
        SchemaVersion = 1,
        PluginInstanceId = "instance",
        AccessToken = "token",
        DiscoveryPath = "discovery.json",
    };
}
