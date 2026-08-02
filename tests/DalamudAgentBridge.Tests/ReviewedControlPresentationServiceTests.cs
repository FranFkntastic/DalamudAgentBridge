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
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("example", "Example Plugin", "select-main-tab", "Example Plugin", 1) },
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
            SurfaceId = "example",
            ControlIds = ["first", "second"],
        }, CancellationToken.None);

        Assert.Equal(42, receipt.FrameId);
        Assert.Equal(["first", "second"], receipt.Controls.Select(control => control.Id));
        Assert.Contains("open-main-window:", commands);
        Assert.Contains("select-main-tab:Example Plugin", commands);
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
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("example", "Example Plugin", "select-main-tab", "Example Plugin", 1) },
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
            SurfaceId = "example",
            ControlId = "example.refresh",
        }, CancellationToken.None);

        Assert.True(receipt.Invocation.Success);
        Assert.Contains("invoke-control:example.refresh:42", commands);
    }

    [Fact]
    public async Task PresentAndInvokeAsync_FallsBackToLegacyReviewControlCommand()
    {
        var commands = new List<string>();
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            commands.Add(command);
            object? receipt = command switch
            {
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("example", "Example Plugin", "open-main-window", "example", 1) },
                "review-control" => new AgentBridgeUiControlReview(
                    42,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Evaluate", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, null)),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = command != "get-control",
                Message = command == "get-control" ? "Bridge command is not allowed." : "ok",
                Receipt = receipt == null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });

        var receipt = await service.PresentAndInvokeAsync(CreateInstance(), new ReviewedControlActionRequest
        {
            SurfaceId = "example",
            ControlId = "example.refresh",
        }, CancellationToken.None);

        Assert.True(receipt.Invocation.Success);
        Assert.Contains("get-control", commands);
        Assert.Contains("review-control", commands);
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
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("example", "Example Plugin", "select-main-tab", "Example Plugin", 1) },
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
        var request = new ReviewedControlPresentationRequest { SurfaceId = "example", ControlIds = ["example.refresh"] };

        await service.PresentAsync(CreateInstance(), request, CancellationToken.None);
        await service.PresentAsync(CreateInstance(), request, CancellationToken.None);

        Assert.Equal(1, commands.Count(command => command == "get-review-surfaces"));
        Assert.Equal(1, commands.Count(command => command == "open-main-window"));
        Assert.Equal(1, commands.Count(command => command == "select-main-tab"));
    }

    [Fact]
    public async Task PresentAsync_ExplainsWhenAnExpiredFrameSuggestsACollapsedWindow()
    {
        var renderedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            object? receipt = command switch
            {
                "get-review-surfaces" => new[] { new AgentBridgeReviewSurfaceDescriptor("example", "Example Plugin", "open-main-window", "example", 1) },
                "get-control" => new AgentBridgeUiControlReview(
                    7,
                    renderedAt,
                    renderedAt.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Evaluate", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, null)),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = true,
                Message = "ok",
                Receipt = receipt == null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => service.PresentAsync(
            CreateInstance(),
            new ReviewedControlPresentationRequest
            {
                SurfaceId = "example",
                ControlIds = ["example.refresh"],
                TimeoutMilliseconds = 250,
            },
            CancellationToken.None));

        Assert.Contains("frame 7", exception.Message, StringComparison.Ordinal);
        Assert.Contains("likely collapsed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PresentAsync_InvalidatesSurfaceCacheWhenCatalogRevisionChanges()
    {
        var revision = 1L;
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            object? receipt = command switch
            {
                "get-manifest" => CreateManifest(revision, revision == 1 ? "first" : "second"),
                "get-control" => new AgentBridgeUiControlReview(
                    revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Control", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, "Ready")),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = receipt is not null,
                Message = "ok",
                Receipt = receipt is null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });

        var first = await service.PresentAsync(CreateInstance(), new ReviewedControlPresentationRequest
        {
            SurfaceId = "first",
            ControlIds = ["action"],
        }, CancellationToken.None);
        revision = 2;
        var second = await service.PresentAsync(CreateInstance(), new ReviewedControlPresentationRequest
        {
            SurfaceId = "second",
            ControlIds = ["action"],
        }, CancellationToken.None);

        Assert.Equal("first", first.SurfaceId);
        Assert.Equal("second", second.SurfaceId);
    }

    [Fact]
    public async Task PresentAndInvokeAsync_ResolvesUniqueActionSurfaceFromManifest()
    {
        var service = new ReviewedControlPresentationService((_, command, request, _) =>
        {
            object? receipt = command switch
            {
                "get-manifest" => CreateManifest(7, "example", "example.refresh"),
                "get-control" => new AgentBridgeUiControlReview(
                    42, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(3),
                    new AgentBridgeUiControl(request!.Target!, "Refresh", AgentBridgeUiControlKind.Button, 0, 0, 1, 1, true, false, "Ready")),
                _ => null,
            };
            return Task.FromResult(new PluginBridgeResponse
            {
                Success = command == "invoke-control" || receipt is not null,
                Message = "ok",
                Receipt = receipt is null ? null : JsonSerializer.SerializeToElement(receipt),
            });
        });

        var result = await service.PresentAndInvokeAsync(CreateInstance(), new ReviewedControlActionRequest
        {
            ControlId = "example.refresh",
        }, CancellationToken.None);

        Assert.Equal("example", result.Presentation.SurfaceId);
        Assert.True(result.Invocation.Success);
    }

    private static AgentBridgeManifest CreateManifest(long revision, string surfaceId, string? actionId = null) => new(
        2,
        new AgentBridgeRuntimeIdentity(
            "Test",
            "1.0.0.0",
            "1.0.0",
            "Test",
            null,
            "ABC",
            "test.dll",
            1,
            "runtime",
            DateTimeOffset.UtcNow),
        "profile",
        "primary",
        "test.snapshot.v1",
        [],
        [new AgentBridgeReviewSurfaceDescriptor(surfaceId, surfaceId, "present-surface", surfaceId, 1)],
        [],
        actionId is null ? [] : [new AgentBridgeActionDescriptor(actionId, actionId, surfaceId, AgentBridgeUiControlKind.Button, true)],
        revision);

    private static BridgeInstance CreateInstance() => new()
    {
        Id = "ExamplePlugin-1",
        PluginName = "ExamplePlugin",
        PluginInternalName = "ExamplePlugin",
        PipeName = "pipe",
        ProcessId = 1,
        SchemaVersion = 1,
        PluginInstanceId = "instance",
        AccessToken = "token",
        DiscoveryPath = "discovery.json",
    };
}
