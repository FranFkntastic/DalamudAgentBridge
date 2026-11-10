using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class ReflectedInputFrameWiringTests
{
    [Fact]
    public void ConnectorRegistersTheFramePumpInsteadOfDispatchingFromDraw()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "DalamudAgentBridge.Plugin", "Plugin.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "DalamudAgentBridge.Plugin", "Plugin.cs"));
        Assert.Contains("new ReflectedPluginWindowInputFramePump(", source);
        Assert.Contains("pluginSurfaceInputPump.EnsureRegistered();", source);
        Assert.Contains("pluginSurfaceInputPump.Dispose();", source);
        Assert.DoesNotContain("pluginSurfaceInput.RenderFrame();", source);
    }
}
