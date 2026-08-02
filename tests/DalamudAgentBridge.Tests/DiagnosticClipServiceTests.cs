using Xunit;
using System.Text.Json;

namespace DalamudAgentBridge.Tests;

public sealed class DiagnosticClipServiceTests
{
    [Theory]
    [InlineData(2, 250)]
    [InlineData(12, 5000)]
    [InlineData(6, 1000)]
    public void AcceptsBoundedClipRequests(int frames, int intervalMilliseconds) =>
        DiagnosticClipService.Validate(new DiagnosticClipRequest(frames, intervalMilliseconds));

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(13, 1000)]
    [InlineData(6, 249)]
    [InlineData(6, 5001)]
    public void RejectsUnboundedClipRequests(int frames, int intervalMilliseconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticClipService.Validate(new DiagnosticClipRequest(frames, intervalMilliseconds)));

    [Fact]
    public void ClipSampleKeepsMovementEvidenceButDropsLargeContextCollections()
    {
        using var document = JsonDocument.Parse("""
            {"schemaVersion":1,"capturedAtUtc":"2026-08-02T00:00:00Z","available":true,
             "client":{"territoryType":129},"character":{"x":1,"y":2,"z":3},
             "activeConditions":["Mounted"],"navigation":{"code":"PathRunning"},
             "nearbyObjects":[{"name":"noise"}],"recentChat":[{"message":"noise"}]}
            """);

        var sample = DiagnosticClipService.CompactSituation(document.RootElement);

        Assert.True(sample.TryGetProperty("character", out _));
        Assert.True(sample.TryGetProperty("navigation", out _));
        Assert.False(sample.TryGetProperty("nearbyObjects", out _));
        Assert.False(sample.TryGetProperty("recentChat", out _));
    }
}
