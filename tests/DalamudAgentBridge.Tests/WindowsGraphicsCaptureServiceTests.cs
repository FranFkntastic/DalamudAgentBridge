namespace DalamudAgentBridge.Tests;

using Xunit;

public sealed class WindowsGraphicsCaptureServiceTests
{
    [Fact]
    public void ResolveMainWindowRejectsInvalidProcessId()
    {
        var exception = Assert.Throws<WindowsGraphicsCaptureException>(
            () => WindowsGraphicsCaptureService.ResolveMainWindow(0));

        Assert.Equal(WindowsGraphicsCaptureFailure.InvalidProcess, exception.Failure);
    }

    [Fact]
    public void ResolveMainWindowMapsMissingProcess()
    {
        var exception = Assert.Throws<WindowsGraphicsCaptureException>(
            () => WindowsGraphicsCaptureService.ResolveMainWindow(int.MaxValue));

        Assert.Equal(WindowsGraphicsCaptureFailure.ProcessNotRunning, exception.Failure);
    }
}
