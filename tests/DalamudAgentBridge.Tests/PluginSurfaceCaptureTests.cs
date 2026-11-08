using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class PluginSurfaceCaptureTests
{
    private static PluginSurfaceWindowMeasurement Sample => new("lease", "RQ", "window", "runtime", 123, 456, 0, 0, 800, 600, -1920, 100, 42);

    [Fact]
    public void CompletedFramesMayAdvanceWithoutChangingTarget()
        => Assert.True(Sample.HasSameTargetAndGeometry(Sample with { CompletedFrame = 45 }));

    [Fact]
    public void RejectsMovedResizedReloadedOrReplacedWindow()
    {
        var changed = new[]
        {
            Sample with { ScreenX = 10 }, Sample with { ScreenY = 0 }, Sample with { Width = 801 },
            Sample with { Height = 601 }, Sample with { X = 1 }, Sample with { Y = 1 },
            Sample with { RuntimeInstanceId = "reloaded" }, Sample with { WindowHandle = 124 },
            Sample with { ViewportId = 457 }, Sample with { TransactionId = "new" },
            Sample with { SurfaceId = "other" }, Sample with { TargetPlugin = "other" },
        };
        foreach (var measurement in changed) Assert.False(Sample.HasSameTargetAndGeometry(measurement));
    }

    [Theory]
    [InlineData(-1, 0, 800, 600)]
    [InlineData(0, -1, 800, 600)]
    [InlineData(0, 0, 0, 600)]
    [InlineData(0, 0, 800, 0)]
    [InlineData(1, 0, 800, 600)]
    [InlineData(0, 1, 800, 600)]
    [InlineData(int.MaxValue, 0, 800, 600)]
    [InlineData(0, int.MaxValue, 800, 600)]
    public void RefusesInvalidOrOverflowingRegions(int x, int y, int width, int height)
        => Assert.Throws<WindowsGraphicsCaptureException>(() => WindowsGraphicsCaptureService.ValidateRegion(x, y, width, height, 800, 600));

    [Fact]
    public void AcceptsExactWindowAndContainedCrop()
    {
        WindowsGraphicsCaptureService.ValidateRegion(0, 0, 800, 600, 800, 600);
        WindowsGraphicsCaptureService.ValidateRegion(10, 20, 100, 100, 800, 600);
    }

    [Fact]
    public void RefusesUnownedOrMissingPlatformWindow()
    {
        Assert.Equal(WindowsGraphicsCaptureFailure.WindowOwnershipMismatch,
            Assert.Throws<WindowsGraphicsCaptureException>(() => WindowsGraphicsCaptureService.ValidateWindowOwnership(Environment.ProcessId, (nint)(-1))).Failure);
        Assert.Equal(WindowsGraphicsCaptureFailure.MainWindowUnavailable,
            Assert.Throws<WindowsGraphicsCaptureException>(() => WindowsGraphicsCaptureService.ValidateWindowOwnership(Environment.ProcessId, 0)).Failure);
    }
}
