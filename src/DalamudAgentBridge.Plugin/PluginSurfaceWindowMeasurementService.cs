using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using System;
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

    public async Task<PluginSurfaceWindowMeasurement> MeasureAsync(string transactionId, CancellationToken cancellationToken)
    {
        var request = new Pending(transactionId);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is not null)
                throw new InvalidOperationException("A plugin window measurement is already pending.");
            pending = request;
        }
        try
        {
            return await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
                if (ReferenceEquals(pending, request)) pending = null;
        }
    }

    public unsafe void RenderFrame()
    {
        lock (gate)
        {
            if (pending is not { } request || request.Completion.Task.IsCompleted) return;
            try
            {
                var target = resolve(request.TransactionId)
                    ?? throw new InvalidOperationException("The presentation lease is stale or its plugin runtime changed.");
                if (!target.Window.IsOpen)
                    throw new InvalidOperationException("The leased plugin window is no longer open.");
                var frame = ImGui.GetFrameCount();
                request.FirstFrame ??= frame;
                var window = ImGuiP.FindWindowByName(new ImU8String(target.Window.WindowName));
                if (window.IsNull || window.Hidden || window.LastFrameActive < request.FirstFrame.Value || frame <= request.FirstFrame.Value)
                    return;
                // At least one target draw has completed since the request, independent of
                // whether DAB's callback runs before or after the target plugin this frame.
                var viewport = ImGui.FindViewportByID(window.ViewportId);
                if (viewport.IsNull || viewport.PlatformHandleRaw == null)
                    throw new InvalidOperationException("The leased plugin viewport has no platform window.");
                request.Completion.TrySetResult(new(
                    request.TransactionId, target.Descriptor.PluginInternalName, target.Descriptor.Id,
                    target.Descriptor.RuntimeInstanceId!, (long)(nint)viewport.PlatformHandleRaw, viewport.ID,
                    checked((int)MathF.Round(window.Pos.X - viewport.Pos.X)),
                    checked((int)MathF.Round(window.Pos.Y - viewport.Pos.Y)),
                    checked((int)MathF.Round(window.Size.X)), checked((int)MathF.Round(window.Size.Y)),
                    checked((int)MathF.Round(viewport.Pos.X)), checked((int)MathF.Round(viewport.Pos.Y)),
                    Math.Min(window.LastFrameActive, frame - 1)));
            }
            catch (Exception exception) { request.Completion.TrySetException(exception); }
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

    private sealed class Pending(string transactionId)
    {
        public string TransactionId { get; } = transactionId;
        public int? FirstFrame { get; set; }
        public TaskCompletionSource<PluginSurfaceWindowMeasurement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
