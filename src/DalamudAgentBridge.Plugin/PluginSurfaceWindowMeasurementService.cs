using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

/// <summary>Measures a leased ImGui window; never reads a GPU render target or emits input.</summary>
internal sealed class PluginSurfaceWindowMeasurementService : IDisposable
{
    private readonly Func<string, ReflectedPluginWindowPresentationTarget?> resolve;
    private readonly object gate = new();
    private Pending? pending;
    private bool disposed;

    public PluginSurfaceWindowMeasurementService(Func<string, ReflectedPluginWindowPresentationTarget?> resolve)
        => this.resolve = resolve;

    public async Task<PluginSurfaceWindowMeasurement> MeasureAsync(string transactionId, int nonce, CancellationToken cancellationToken)
    {
        var request = new Pending(transactionId, nonce);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is { } active && !active.Completion.Task.IsCompleted)
                throw new InvalidOperationException("A plugin window measurement is already pending.");
            pending = request;
        }
        try
        {
            return await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (gate)
                if (ReferenceEquals(pending, request)) pending = null;
            throw;
        }
    }

    public unsafe void RenderFrame()
    {
        lock (gate)
        {
            if (pending is not { } request) return;
            try
            {
                if (Stopwatch.GetElapsedTime(request.Start) >= TimeSpan.FromSeconds(4))
                    throw new TimeoutException("Capture stamp expired.");
                var target = resolve(request.TransactionId)
                    ?? throw new InvalidOperationException("The presentation lease is stale or its plugin runtime changed.");
                if (!target.Window.IsOpen)
                    throw new InvalidOperationException("The leased plugin window is no longer open.");
                var frame = ImGui.GetFrameCount();
                request.FirstFrame ??= frame;
                var window = ImGuiP.FindWindowByName(new ImU8String(target.Window.WindowName));
                if (window.IsNull || window.Hidden || window.LastFrameActive < frame - 1)
                    return;
                var viewport = ImGui.FindViewportByID(window.ViewportId);
                if (viewport.IsNull || viewport.PlatformHandleRaw == null)
                    throw new InvalidOperationException("The leased plugin viewport has no platform window.");
                var width = checked((int)MathF.Round(window.Size.X));
                var height = checked((int)MathF.Round(window.Size.Y));
                var columns = Math.Clamp(width / CaptureFreshnessStamp.CellSize, 1, CaptureFreshnessStamp.Cells);
                var stamp = new CaptureFreshnessStamp(request.Nonce, 0, 0, columns);
                if (width < stamp.Width || height < stamp.Height)
                    throw new InvalidOperationException("The leased window is too small for a freshness stamp.");
                stamp = stamp with { Y = height - stamp.Height };
                var measured = new PluginSurfaceWindowMeasurement(
                    request.TransactionId, target.Descriptor.PluginInternalName, target.Descriptor.Id,
                    target.Descriptor.RuntimeInstanceId!, (long)(nint)viewport.PlatformHandleRaw, viewport.ID,
                    checked((int)MathF.Round(window.Pos.X - viewport.Pos.X)),
                    checked((int)MathF.Round(window.Pos.Y - viewport.Pos.Y)),
                    width, height,
                    checked((int)MathF.Round(viewport.Pos.X)), checked((int)MathF.Round(viewport.Pos.Y)),
                    frame, stamp);
                var draw = ImGui.GetForegroundDrawList(viewport);
                var origin = viewport.Pos + new Vector2(measured.X, measured.Y);
                for (var i = 0; i < CaptureFreshnessStamp.Cells; i++)
                {
                    var min = origin + new Vector2(stamp.CellX(i), stamp.CellY(i));
                    draw.AddRectFilled(min, min + new Vector2(CaptureFreshnessStamp.CellSize), stamp.White(i) ? 0xffffffffu : 0xff000000u);
                }
                // A previous stamped frame must have finished, with the leased window active.
                if (request.LastDraw is { } last && frame > request.FirstFrame.Value && frame == last.CompletedFrame + 1 &&
                    (request.TargetDrewInLastStampFrame || window.LastFrameActive == last.CompletedFrame) &&
                    measured.HasSameTargetAndGeometry(last))
                    request.Completion.TrySetResult(last);
                request.LastDraw = measured;
                request.TargetDrewInLastStampFrame = window.LastFrameActive == frame;
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
                pending = null;
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Completion.TrySetCanceled();
            pending = null;
        }
    }

    private sealed class Pending(string transactionId, int nonce)
    {
        public string TransactionId { get; } = transactionId;
        public int Nonce { get; } = nonce;
        public long Start { get; } = Stopwatch.GetTimestamp();
        public PluginSurfaceWindowMeasurement? LastDraw { get; set; }
        public bool TargetDrewInLastStampFrame { get; set; }
        public int? FirstFrame { get; set; }
        public TaskCompletionSource<PluginSurfaceWindowMeasurement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
