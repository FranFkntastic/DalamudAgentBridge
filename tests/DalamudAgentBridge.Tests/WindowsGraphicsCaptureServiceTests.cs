namespace DalamudAgentBridge.Tests;

using System.Diagnostics;
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

    [Fact]
    public void ValidateWindowOwnershipRejectsEmptyHandle()
    {
        var exception = Assert.Throws<WindowsGraphicsCaptureException>(
            () => WindowsGraphicsCaptureService.ValidateWindowOwnership(Environment.ProcessId, nint.Zero));

        Assert.Equal(WindowsGraphicsCaptureFailure.MainWindowUnavailable, exception.Failure);
    }

    [Fact]
    public void ValidateWindowOwnershipRejectsForeignWindow()
    {
        var currentProcess = Process.GetCurrentProcess();
        if (currentProcess.MainWindowHandle == nint.Zero)
            return;

        var exception = Assert.Throws<WindowsGraphicsCaptureException>(
            () => WindowsGraphicsCaptureService.ValidateWindowOwnership(int.MaxValue, currentProcess.MainWindowHandle));

        Assert.Equal(WindowsGraphicsCaptureFailure.ProcessNotRunning, exception.Failure);
    }

}
