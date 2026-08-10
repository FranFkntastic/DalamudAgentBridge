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
    public void ClipSampleKeepsOnlyExplicitLocationAndResourceEvidence()
    {
        using var document = JsonDocument.Parse("""
            {"schemaVersion":1,"capturedAtUtc":"2026-08-02T00:00:00Z","available":true,
             "client":{"territoryType":129,"mapId":11,"instance":2,"language":"English"},
             "character":{"name":"Private Name","currentWorld":"Private World","entityId":44,
              "x":1,"y":2,"z":3,"mapCoordinates":{"x":4,"y":5,"z":6,"future":"drop"},
              "currentHp":100,"maxHp":200,"statuses":[{"sourceId":77}]},
             "activeConditions":["Mounted"],"navigation":{"code":"PathRunning"},
             "nearbyObjects":[{"name":"noise"}],"recentChat":[{"message":"noise"}]}
            """);

        var sample = DiagnosticClipService.CompactSituation(document.RootElement);

        Assert.True(sample.TryGetProperty("location", out var location));
        Assert.True(sample.TryGetProperty("resources", out var resources));
        Assert.True(sample.TryGetProperty("navigation", out _));
        Assert.Equal(1, location.GetProperty("x").GetInt32());
        Assert.Equal(100, resources.GetProperty("currentHp").GetInt32());
        Assert.False(sample.TryGetProperty("character", out _));
        Assert.DoesNotContain("Private Name", sample.GetRawText());
        Assert.DoesNotContain("Private World", sample.GetRawText());
        Assert.DoesNotContain("sourceId", sample.GetRawText());
        Assert.DoesNotContain("future", sample.GetRawText());
        Assert.False(sample.TryGetProperty("nearbyObjects", out _));
        Assert.False(sample.TryGetProperty("recentChat", out _));
    }
}
