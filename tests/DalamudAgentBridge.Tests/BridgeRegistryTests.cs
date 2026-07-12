using Microsoft.Extensions.Configuration;
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

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
