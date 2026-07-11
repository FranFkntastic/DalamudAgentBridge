using Microsoft.Graphics.Canvas;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using WinRT;

namespace DalamudAgentBridge;

public sealed class WindowsGraphicsCaptureService
{
    private const int MaximumDimension = 16384;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public async Task<WindowsGraphicsCapture> CaptureAsync(
        int processId,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var windowHandle = ResolveMainWindow(processId);
        GraphicsCaptureItem item;
        try { item = CaptureItemFactory.CreateForWindow(windowHandle); }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new WindowsGraphicsCaptureException(
                WindowsGraphicsCaptureFailure.TargetUnsupported,
                "Windows Graphics Capture could not create a capture item for the target window.", ex);
        }

        ValidateDimensions(item.Size.Width, item.Size.Height, "target window");
        using var device = CanvasDevice.GetSharedDevice();
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = framePool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            session.IsCursorCaptureEnabled = false;

        var received = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
        {
            var frame = sender.TryGetNextFrame();
            if (frame != null && !received.TrySetResult(frame))
                frame.Dispose();
        }

        framePool.FrameArrived += OnFrameArrived;
        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var registration = linkedSource.Token.Register(() => received.TrySetCanceled(linkedSource.Token));
        try
        {
            session.StartCapture();
            using var frame = await received.Task.ConfigureAwait(false);
            var width = frame.ContentSize.Width;
            var height = frame.ContentSize.Height;
            ValidateDimensions(width, height, "captured frame");
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
            using var stream = new InMemoryRandomAccessStream();
            await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask(linkedSource.Token).ConfigureAwait(false);
            if (stream.Size is 0 or > int.MaxValue)
                throw new WindowsGraphicsCaptureException(
                    WindowsGraphicsCaptureFailure.InvalidFrame,
                    "Windows Graphics Capture produced an invalid encoded frame size.");
            var bytes = new byte[(int)stream.Size];
            try
            {
                stream.Seek(0);
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                var loaded = await reader.LoadAsync((uint)bytes.Length).AsTask(linkedSource.Token).ConfigureAwait(false);
                if (loaded != bytes.Length)
                    throw new WindowsGraphicsCaptureException(
                        WindowsGraphicsCaptureFailure.InvalidFrame,
                        "Windows Graphics Capture returned an incomplete encoded frame.");
                reader.ReadBytes(bytes);
                return new WindowsGraphicsCapture(bytes, width, height);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new WindowsGraphicsCaptureException(
                WindowsGraphicsCaptureFailure.TimedOut,
                "Windows Graphics Capture did not produce a frame before the timeout.", ex);
        }
        catch (WindowsGraphicsCaptureException) { throw; }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            throw new WindowsGraphicsCaptureException(
                WindowsGraphicsCaptureFailure.CaptureFailed,
                "Windows Graphics Capture failed while acquiring or encoding the frame.", ex);
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
        }
    }

    internal static nint ResolveMainWindow(int processId)
    {
        if (processId <= 0)
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.InvalidProcess, "The target process ID is invalid.");
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.ProcessNotRunning, "The target process is not running.");
            var handle = process.MainWindowHandle;
            if (handle == nint.Zero)
                throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.MainWindowUnavailable, "The target process has no main window.");
            GetWindowThreadProcessId(handle, out var ownerProcessId);
            if (ownerProcessId != processId)
                throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.WindowOwnershipMismatch, "The target main-window ownership could not be verified.");
            return handle;
        }
        catch (ArgumentException ex)
        {
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.ProcessNotRunning, "The target process is not running.", ex);
        }
    }

    private static void ValidateDimensions(int width, int height, string source)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.InvalidFrame, $"The {source} has invalid dimensions for capture.");
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out int processId);

    private static class CaptureItemFactory
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
}

public sealed record WindowsGraphicsCapture(byte[] PngBytes, int Width, int Height);

public enum WindowsGraphicsCaptureFailure
{
    InvalidProcess,
    ProcessNotRunning,
    MainWindowUnavailable,
    WindowOwnershipMismatch,
    TargetUnsupported,
    InvalidFrame,
    TimedOut,
    CaptureFailed,
}

public sealed class WindowsGraphicsCaptureException : Exception
{
    public WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure failure, string message, Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public WindowsGraphicsCaptureFailure Failure { get; }
}
