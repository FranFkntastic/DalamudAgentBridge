using Microsoft.Graphics.Canvas;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: WgcProbe <process-id|hwnd:handle> <output-png>");
    return 2;
}

Process? process = null;
nint windowHandle;
if (args[0].StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase) && long.TryParse(args[0][5..], out var rawHandle))
{
    windowHandle = (nint)rawHandle;
}
else if (int.TryParse(args[0], out var processId))
{
    process = Process.GetProcessById(processId);
    windowHandle = process.MainWindowHandle;
}
else
{
    Console.Error.WriteLine("Capture target must be a process ID or hwnd:handle.");
    return 2;
}
if (windowHandle == nint.Zero)
    throw new InvalidOperationException("Target process has no main window.");

var item = CaptureItemFactory.CreateForWindow(windowHandle);
if (item.Size.Width < 1 || item.Size.Height < 1)
    throw new InvalidOperationException("Target window has no capturable extent.");

using var device = CanvasDevice.GetSharedDevice();
using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
    device,
    DirectXPixelFormat.B8G8R8A8UIntNormalized,
    1,
    item.Size);
using var session = framePool.CreateCaptureSession(item);
if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
    session.IsCursorCaptureEnabled = false;

var frameReceived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
{
    var frame = sender.TryGetNextFrame();
    if (frame != null && !frameReceived.TrySetResult(frame))
        frame.Dispose();
}

framePool.FrameArrived += OnFrameArrived;
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var registration = timeout.Token.Register(() => frameReceived.TrySetCanceled(timeout.Token));
    session.StartCapture();
    using var frame = await frameReceived.Task.ConfigureAwait(false);
    using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
    await bitmap.SaveAsync(args[1]).AsTask(timeout.Token).ConfigureAwait(false);
    Console.WriteLine($"{frame.ContentSize.Width}x{frame.ContentSize.Height}");
    return 0;
}
finally
{
    framePool.FrameArrived -= OnFrameArrived;
    process?.Dispose();
}

internal static class CaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemInterfaceId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForWindow(nint windowHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemInterfaceId;
        var result = interop.CreateForWindow(windowHandle, ref iid, out var item);
        Marshal.ThrowExceptionForHR(result);
        try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(item); }
        finally { Marshal.Release(item); }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, ref Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }
}
