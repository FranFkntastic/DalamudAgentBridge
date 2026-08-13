using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class BridgeRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"bridge-registry-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultDiscoveryIncludesEveryXivLauncherProfile()
    {
        Directory.CreateDirectory(Path.Combine(root, "XIVLauncher", "pluginConfigs"));
        Directory.CreateDirectory(Path.Combine(root, "XIVLauncher-Multibox-2", "pluginConfigs"));
        Directory.CreateDirectory(Path.Combine(root, "Unrelated", "pluginConfigs"));
        var configuration = new ConfigurationBuilder().Build();

        var roots = BridgeRegistry.ResolvePluginConfigRoots(configuration, root);

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, path => path.Contains("XIVLauncher-Multibox-2", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveUsesStableProfileAliasAndAdvertisedInternalName()
    {
        var profileRoot = Path.Combine(root, "XIVLauncher", "pluginConfigs");
        var pluginDirectory = Path.Combine(profileRoot, "Friendly Directory");
        var bridgeDirectory = Path.Combine(pluginDirectory, "agent-bridge");
        Directory.CreateDirectory(bridgeDirectory);
        File.WriteAllText(Path.Combine(profileRoot, "Friendly Directory.json"), "{\"AgentBridgeAccessToken\":\"test-token\"}");
        File.WriteAllText(Path.Combine(bridgeDirectory, "discovery.json"), JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            PipeName = "test-pipe",
            ProcessId = Environment.ProcessId,
            PluginInstanceId = "instance",
            RuntimeInstanceId = "runtime",
            PluginInternalName = "ExamplePlugin",
            ProfileId = "profile-primary",
            ProfileAlias = "primary",
            ProtocolVersion = 2,
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bridge:PluginConfigRoot"] = profileRoot })
            .Build();

        var instance = new BridgeRegistry(configuration).Resolve(new BridgeTargetSelector("ExamplePlugin", "primary"));

        Assert.Equal("Friendly Directory", instance.PluginName);
        Assert.Equal("ExamplePlugin", instance.PluginInternalName);
        Assert.Equal("profile-primary", instance.ProfileId);
        Assert.Equal(2, instance.ProtocolVersion);
    }

    [Fact]
    public void ResolvePrefersNewestCurrentDiscoveryWhenPidWasReusedAcrossProfiles()
    {
        var primaryRoot = Path.Combine(root, "XIVLauncher", "pluginConfigs");
        var tertiaryRoot = Path.Combine(root, "XIVLauncher-Multibox-3", "pluginConfigs");
        var processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        WriteDiscovery(primaryRoot, "primary", "old-runtime", processStart.AddSeconds(1));
        WriteDiscovery(tertiaryRoot, "XIVLauncher-Multibox-3", "new-runtime", processStart.AddSeconds(2));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:PluginConfigRoots"] = $"{tertiaryRoot}{Path.PathSeparator}{primaryRoot}",
            })
            .Build();

        var instance = new BridgeRegistry(configuration).Resolve(
            new BridgeTargetSelector("ExamplePlugin", "XIVLauncher-Multibox-3", Environment.ProcessId));

        Assert.Equal("new-runtime", instance.RuntimeInstanceId);
        Assert.Equal("XIVLauncher-Multibox-3", instance.ProfileAlias);
    }

    [Fact]
    public void DiscoverRejectsDiscoveryOlderThanTheLiveProcess()
    {
        var profileRoot = Path.Combine(root, "XIVLauncher", "pluginConfigs");
        WriteDiscovery(
            profileRoot,
            "primary",
            "stale-runtime",
            Process.GetCurrentProcess().StartTime.ToUniversalTime().AddMinutes(-1));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bridge:PluginConfigRoot"] = profileRoot })
            .Build();

        var instances = new BridgeRegistry(configuration).Discover();

        Assert.DoesNotContain(instances, instance => instance.RuntimeInstanceId == "stale-runtime");
    }

    private static void WriteDiscovery(
        string profileRoot,
        string profileAlias,
        string runtimeInstanceId,
        DateTime updatedAtUtc)
    {
        var pluginDirectory = Path.Combine(profileRoot, "ExamplePlugin");
        var bridgeDirectory = Path.Combine(pluginDirectory, "agent-bridge");
        Directory.CreateDirectory(bridgeDirectory);
        File.WriteAllText(Path.Combine(profileRoot, "ExamplePlugin.json"), "{\"AgentBridgeAccessToken\":\"test-token\"}");
        var discoveryPath = Path.Combine(bridgeDirectory, $"discovery-{Environment.ProcessId}.json");
        File.WriteAllText(discoveryPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            PipeName = $"test-pipe-{profileAlias}",
            ProcessId = Environment.ProcessId,
            PluginInstanceId = "instance",
            RuntimeInstanceId = runtimeInstanceId,
            PluginInternalName = "ExamplePlugin",
            ProfileId = $"profile-{profileAlias}",
            ProfileAlias = profileAlias,
            ProtocolVersion = 2,
        }));
        File.SetLastWriteTimeUtc(discoveryPath, updatedAtUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
