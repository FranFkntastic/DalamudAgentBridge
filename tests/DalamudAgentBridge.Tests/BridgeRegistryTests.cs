using Microsoft.Extensions.Configuration;
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
            PluginInternalName = "Quartermaster",
            ProfileId = "profile-primary",
            ProfileAlias = "primary",
            ProtocolVersion = 2,
        }));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bridge:PluginConfigRoot"] = profileRoot })
            .Build();

        var instance = new BridgeRegistry(configuration).Resolve(new BridgeTargetSelector("Quartermaster", "primary"));

        Assert.Equal("Friendly Directory", instance.PluginName);
        Assert.Equal("Quartermaster", instance.PluginInternalName);
        Assert.Equal("profile-primary", instance.ProfileId);
        Assert.Equal(2, instance.ProtocolVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
