using DalamudAgentBridge.Mcp;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class AgentBridgeToolsTests
{
    [Fact]
    public void ClipFrameMissingFromVaultFailsTheTool()
    {
        var vault = new ReviewVault(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var capturedAt = DateTimeOffset.UtcNow;
        var capture = new PluginCaptureReviewReceipt(
            new BridgeInstanceView("bridge", "plugin", "pipe", 1, 1, "instance", "plugin", null, null, null, 1),
            new BridgeCaptureReceipt { CaptureId = "capture", CapturedAtUtc = capturedAt },
            new ReviewCapture(new string('A', 64), new BridgeCaptureReceipt { CaptureId = "capture", CapturedAtUtc = capturedAt }, capturedAt.AddMinutes(1)),
            string.Empty);
        var frame = new DiagnosticClipFrame(3, capturedAt, default, capture);

        var exception = Assert.Throws<InvalidOperationException>(() => AgentBridgeTools.RequireClipFrame(vault, frame));

        Assert.Contains("clip frame 3", exception.Message);
    }
}
