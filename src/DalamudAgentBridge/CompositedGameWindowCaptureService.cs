using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DalamudAgentBridge;

/// <summary>
/// Captures the final desktop-composited FFXIV client area. This is deliberately limited to
/// the foreground client window so an obscured game cannot accidentally capture another app.
/// </summary>
public sealed class CompositedGameWindowCaptureService
{
    public CompositedWindowCapture Capture(int processId)
    {
        var windowHandle = GetForegroundTargetWindow(processId);
        if (!GetClientRect(windowHandle, out var clientRect))
            throw new InvalidOperationException("The FFXIV client area could not be resolved for composited capture.");

        var topLeft = clientRect.TopLeft;
        var bottomRight = clientRect.BottomRight;
        if (!ClientToScreen(windowHandle, ref topLeft) || !ClientToScreen(windowHandle, ref bottomRight))
            throw new InvalidOperationException("The FFXIV client area could not be converted to screen coordinates for composited capture.");

        var width = bottomRight.X - topLeft.X;
        var height = bottomRight.Y - topLeft.Y;
        if (width is < 1 or > 16384 || height is < 1 or > 16384)
            throw new InvalidOperationException("The FFXIV client area has invalid dimensions for composited capture.");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(topLeft.X, topLeft.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return new CompositedWindowCapture(output.ToArray(), width, height);
    }

    public bool IsForegroundTarget(int processId)
    {
        try { _ = GetForegroundTargetWindow(processId); return true; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static nint GetForegroundTargetWindow(int processId)
    {
        using var process = Process.GetProcessById(processId);
        var windowHandle = process.MainWindowHandle;
        if (windowHandle == nint.Zero)
            throw new InvalidOperationException("The game process does not currently expose a main window.");
        if (GetForegroundWindow() != windowHandle)
            throw new InvalidOperationException("The FFXIV client must be the foreground window before composited capture.");

        GetWindowThreadProcessId(windowHandle, out var foregroundProcessId);
        if (foregroundProcessId != processId)
            throw new InvalidOperationException("Foreground-window ownership could not be verified for composited capture.");
        return windowHandle;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Point TopLeft => new(Left, Top);

        public Point BottomRight => new(Right, Bottom);
    }
}

public sealed record CompositedWindowCapture(byte[] PngBytes, int Width, int Height);
