using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class CaptureSurfaceDiscoveryServiceTests
{
    [Fact]
    public async Task ResolveAsync_UsesProviderAdvertisedDefaultWithoutProductKnowledge()
    {
        var service = CreateService(
            new("plugin.secondary", "Secondary", 20),
            new("plugin.primary", "Primary", 10, IsDefault: true));

        var surface = await service.ResolveAsync(CreateInstance("UnfamiliarPlugin"), null, CancellationToken.None);

        Assert.Equal("plugin.primary", surface.Id);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTargetNotAdvertisedByProvider()
    {
        var service = CreateService(new AgentBridgeCaptureSurfaceDescriptor("plugin.primary", "Primary", 10, IsDefault: true));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveAsync(CreateInstance("UnfamiliarPlugin"), "another-plugin.window", CancellationToken.None));

        Assert.Contains("not advertised", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_RejectsAmbiguousProviderDefaults()
    {
        var service = CreateService(
            new("plugin.first", "First", 10, IsDefault: true),
            new("plugin.second", "Second", 20, IsDefault: true));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetAsync(CreateInstance("UnfamiliarPlugin"), CancellationToken.None));

        Assert.Contains("more than one default", exception.Message, StringComparison.Ordinal);
    }

    private static CaptureSurfaceDiscoveryService CreateService(params AgentBridgeCaptureSurfaceDescriptor[] surfaces) =>
        new((_, command, _, _) => Task.FromResult(new PluginBridgeResponse
        {
            Success = command == "get-capture-surfaces",
            Message = "ok",
            Receipt = JsonSerializer.SerializeToElement(surfaces),
        }));

    private static BridgeInstance CreateInstance(string pluginName) => new()
    {
        Id = $"{pluginName}-1",
        PluginName = pluginName,
        PluginInternalName = pluginName,
        PipeName = "pipe",
        ProcessId = 1,
        SchemaVersion = 1,
        PluginInstanceId = "instance",
        AccessToken = "token",
        DiscoveryPath = "discovery.json",
    };
}
