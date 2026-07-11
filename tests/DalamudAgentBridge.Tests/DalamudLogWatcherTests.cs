using System.Text;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class DalamudLogWatcherTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dab-log-{Guid.NewGuid():N}");

    [Fact]
    public void Read_UsesIndependentByteCursorAndReturnsOnlyCompleteLines()
    {
        var instance = CreateInstance();
        var logPath = DalamudLogWatcher.ResolveLogPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "one\ntwo\npartial", new UTF8Encoding(false));

        var watcher = new DalamudLogWatcher();
        var first = watcher.Read(instance, 0, 20);
        Assert.Equal(["one", "two"], first.Lines);
        Assert.Equal(8, first.NextCursor);

        File.AppendAllText(logPath, " line\nthree\n", new UTF8Encoding(false));
        var second = watcher.Read(instance, first.NextCursor, 20);
        Assert.Equal(["partial line", "three"], second.Lines);
    }

    [Fact]
    public void Read_ResetCursorAfterRotation()
    {
        var instance = CreateInstance();
        var logPath = DalamudLogWatcher.ResolveLogPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "new\n", new UTF8Encoding(false));

        var result = new DalamudLogWatcher().Read(instance, 500, 20);

        Assert.True(result.Reset);
        Assert.Equal(["new"], result.Lines);
        Assert.Equal(4, result.NextCursor);
    }

    [Fact]
    public void Read_EstablishedCursorDoesNotSkipEntriesBeyondLimit()
    {
        var instance = CreateInstance();
        var logPath = DalamudLogWatcher.ResolveLogPath(instance);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "one\ntwo\nthree\n", new UTF8Encoding(false));

        var watcher = new DalamudLogWatcher();
        var first = watcher.Read(instance, 0, 1);
        var second = watcher.Read(instance, first.NextCursor, 1);

        Assert.Equal(["one"], first.Lines);
        Assert.Equal(4, first.NextCursor);
        Assert.Equal(["two"], second.Lines);
        Assert.Equal(8, second.NextCursor);
    }

    private BridgeInstance CreateInstance()
    {
        var discoveryPath = Path.Combine(root, "pluginConfigs", "DalamudAgentBridge", "agent-bridge", "discovery.json");
        return new BridgeInstance
        {
            Id = "DalamudAgentBridge-1",
            PluginName = "DalamudAgentBridge",
            PipeName = "test",
            ProcessId = 1,
            SchemaVersion = 1,
            PluginInstanceId = "test",
            AccessToken = "test",
            DiscoveryPath = discoveryPath,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
