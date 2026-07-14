using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class LocalPluginBuildReplacementServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "DalamudAgentBridge.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReplaceAsync_DisablesCopiesVerifiesAndRestoresRequestedState()
    {
        var instance = CreateInstance();
        var plugin = CreatePlugin(isLoaded: true);
        var lifecycle = new FakePluginLifecycleClient(plugin);
        var service = new LocalPluginBuildReplacementService(lifecycle);
        var installed = LocalPluginBuildReplacementService.ResolveInstalledPluginDirectory(instance, plugin);
        CreateBuild(installed, plugin.InternalName, "old");
        var source = Path.Combine(root, "source");
        CreateBuild(source, plugin.InternalName, "new");

        var receipt = await service.ReplaceAsync(instance, plugin.InternalName, new LocalPluginBuildReplacementRequest
        {
            SourceDirectory = source,
            ExpectedCurrentVersion = plugin.Version,
            ExpectedMainDllSha256 = Hash(Path.Combine(source, $"{plugin.InternalName}.dll")),
        }, CancellationToken.None);

        Assert.Equal([false, true], lifecycle.RequestedStates);
        Assert.True(receipt.WasLoaded);
        Assert.True(receipt.IsLoaded);
        Assert.Equal("new", File.ReadAllText(Path.Combine(installed, $"{plugin.InternalName}.dll")));
        using var installedManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(installed, $"{plugin.InternalName}.json")));
        Assert.Equal(plugin.InternalName, installedManifest.RootElement.GetProperty("InternalName").GetString());
        Assert.Equal("old", installedManifest.RootElement.GetProperty("Payload").GetString());
        Assert.Equal(receipt.InstalledMainDllSha256, Hash(Path.Combine(installed, $"{plugin.InternalName}.dll")));
    }

    [Fact]
    public void ResolveInstalledPluginDirectory_StaysInsideTheDiscoveredLauncherProfile()
    {
        var instance = CreateInstance();
        var plugin = CreatePlugin(isLoaded: false);

        var result = LocalPluginBuildReplacementService.ResolveInstalledPluginDirectory(instance, plugin);

        Assert.Equal(
            Path.Combine(root, "profile", "installedPlugins", plugin.InternalName, plugin.Version),
            result);
    }

    [Fact]
    public async Task ReplaceAsync_RejectsMismatchedManifestBeforeDisablingPlugin()
    {
        var instance = CreateInstance();
        var plugin = CreatePlugin(isLoaded: true);
        var lifecycle = new FakePluginLifecycleClient(plugin);
        var service = new LocalPluginBuildReplacementService(lifecycle);
        var installed = LocalPluginBuildReplacementService.ResolveInstalledPluginDirectory(instance, plugin);
        CreateBuild(installed, plugin.InternalName, "old");
        var source = Path.Combine(root, "bad-source");
        CreateBuild(source, "AnotherPlugin", "new");
        File.Move(Path.Combine(source, "AnotherPlugin.dll"), Path.Combine(source, $"{plugin.InternalName}.dll"));
        File.Move(Path.Combine(source, "AnotherPlugin.json"), Path.Combine(source, $"{plugin.InternalName}.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReplaceAsync(
            instance,
            plugin.InternalName,
            new LocalPluginBuildReplacementRequest { SourceDirectory = source },
            CancellationToken.None));

        Assert.Empty(lifecycle.RequestedStates);
    }

    [Fact]
    public async Task ReplaceAsync_RestoresOriginalFilesAndLoadStateWhenNewBuildFailsToLoad()
    {
        var instance = CreateInstance();
        var plugin = CreatePlugin(isLoaded: true);
        var lifecycle = new FakePluginLifecycleClient(plugin) { FailNextEnable = true };
        var service = new LocalPluginBuildReplacementService(lifecycle);
        var installed = LocalPluginBuildReplacementService.ResolveInstalledPluginDirectory(instance, plugin);
        CreateBuild(installed, plugin.InternalName, "old");
        var source = Path.Combine(root, "failing-source");
        CreateBuild(source, plugin.InternalName, "new");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplaceAsync(
            instance,
            plugin.InternalName,
            new LocalPluginBuildReplacementRequest { SourceDirectory = source },
            CancellationToken.None));

        Assert.Equal([false, true, true], lifecycle.RequestedStates);
        Assert.Equal("old", File.ReadAllText(Path.Combine(installed, $"{plugin.InternalName}.dll")));
        Assert.True((await lifecycle.ListAsync(instance, CancellationToken.None)).Plugins.Single().IsLoaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private BridgeInstance CreateInstance()
    {
        var discovery = Path.Combine(root, "profile", "pluginConfigs", "DalamudAgentBridge", "agent-bridge", "discovery-1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(discovery)!);
        File.WriteAllText(discovery, "{}");
        return new BridgeInstance
        {
            Id = "bridge-1",
            PluginName = "DalamudAgentBridge",
            PipeName = "unused",
            ProcessId = 1,
            SchemaVersion = 1,
            PluginInstanceId = "test",
            AccessToken = "test",
            DiscoveryPath = discovery,
        };
    }

    private static InstalledPluginState CreatePlugin(bool isLoaded) => new()
    {
        InternalName = "ExamplePlugin",
        Name = "Example Plugin",
        Version = "1.2.3.4",
        IsLoaded = isLoaded,
    };

    private static void CreateBuild(string directory, string internalName, string dllContents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{internalName}.dll"), dllContents);
        File.WriteAllText(Path.Combine(directory, $"{internalName}.json"), JsonSerializer.Serialize(new { InternalName = internalName, Payload = dllContents }));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path)))).ToLowerInvariant();

    private sealed class FakePluginLifecycleClient(InstalledPluginState initial) : IPluginLifecycleClient
    {
        private InstalledPluginState state = initial;
        public List<bool> RequestedStates { get; } = [];
        public bool FailNextEnable { get; init; }
        private bool enableFailureConsumed;

        public Task<InstalledPluginSnapshot> ListAsync(BridgeInstance instance, CancellationToken cancellationToken) =>
            Task.FromResult(new InstalledPluginSnapshot { CapturedAtUtc = DateTimeOffset.UtcNow, Plugins = [state] });

        public Task<PluginBridgeResponse> SetEnabledAsync(BridgeInstance instance, string internalName, bool enabled, CancellationToken cancellationToken)
        {
            RequestedStates.Add(enabled);
            if (enabled && FailNextEnable && !enableFailureConsumed)
            {
                enableFailureConsumed = true;
                throw new InvalidOperationException("Simulated load failure.");
            }
            state = state with { IsLoaded = enabled };
            return Task.FromResult(new PluginBridgeResponse { Success = true, Message = "Changed" });
        }
    }
}
