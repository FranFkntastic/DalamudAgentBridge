using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class DabCommandLineTests
{
    [Fact]
    public void SequenceFilePreservesEmbeddedQuotes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"steps":[{"kind":"text","text":"say \"hello\""}]}""");
            var sequence = DabCommandLine.Parse(["surface-input", "--sequence-file", path]).InputSequence();
            Assert.Equal("say \"hello\"", sequence.Steps.Single().Text);
            Assert.Throws<ArgumentException>(() => DabCommandLine.Parse(
                ["surface-input", "--sequence-file", path, "--sequence", "{}"]).InputSequence());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScreenSelectsConnectorWithoutPluginPositional()
    {
        var command = DabCommandLine.Parse(["screen", "--profile", "example", "--pid", "42", "--out", "capture.png"]);
        Assert.Equal("DalamudAgentBridge", command.ConnectorTarget().Plugin);
        Assert.Equal("example", command.Profile());
        Assert.Equal(42, command.ProcessId());
        Assert.Equal("capture.png", command.Value("out"));
    }
}
