using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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
                var mainStamp = stamp with { X = measured.X + stamp.X, Y = measured.Y + stamp.Y };
                DrawStamp(viewport, mainStamp);
                var stamps = new Dictionary<long, List<CaptureFreshnessStamp>> { [measured.WindowHandle] = [mainStamp] };
                var attached = new List<PopupRegion>();
                var stack = ImGui.GetCurrentContext().OpenPopupStack;
                var previousFrameActive = true;
                for (var i = 0; i < stack.Size; i++)
                {
                    ImGuiWindowPtr popup = stack[i].Window;
                    if (popup.IsNull || popup.Hidden || popup.LastFrameActive < frame - 1 ||
                        (!popup.Active && !popup.WasActive) || popup.ID == window.ID) continue;
                    if (!BelongsTo(popup, window)) continue;
                    if (attached.Count >= 8)
                        throw new InvalidOperationException("The leased popup tree exceeds the eight-window capture limit.");
                    previousFrameActive &= popup.WasActive;
                    var name = Marshal.PtrToStringUTF8((nint)popup.Name) ?? $"popup {popup.ID:X8}";
                    var vp = ImGui.FindViewportByID(popup.ViewportId);
                    if (vp.IsNull || vp.PlatformHandleRaw == null)
                        throw new InvalidOperationException($"Popup '{name}' has no platform window.");
                    var handle = (long)(nint)vp.PlatformHandleRaw;
                    int Pixel(float value) => checked((int)MathF.Round(value));
                    var x = Pixel(popup.Pos.X - vp.Pos.X);
                    var y = Pixel(popup.Pos.Y - vp.Pos.Y);
                    var w = Pixel(popup.Size.X);
                    var h = Pixel(popup.Size.Y);
                    if (!stamps.TryGetValue(handle, out var existing)) stamps.Add(handle, existing = []);
                    var marker = PlaceStamp(request.Nonce, x, y, w, h, name, existing);
                    if (!existing.Contains(marker)) { DrawStamp(vp, marker); existing.Add(marker); }
                    attached.Add(new(popup.ID, name, handle, vp.ID, x, y, w, h, Pixel(vp.Pos.X), Pixel(vp.Pos.Y), marker));
                }
                measured = measured with { Attached = attached.ToArray() };
                // A previous stamped frame must have finished, with the leased window active.
                if (request.LastDraw is { } last && frame > request.FirstFrame.Value && frame == last.CompletedFrame + 1 &&
                    (request.TargetDrewInLastStampFrame || window.LastFrameActive == last.CompletedFrame) &&
                    previousFrameActive && measured.HasSameTargetAndGeometry(last))
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

    private static unsafe bool BelongsTo(ImGuiWindowPtr popup, ImGuiWindowPtr leased)
    {
        for (var p = popup.ParentWindow; !p.IsNull; p = p.ParentWindow)
            if (p.Handle == leased.Handle) return true;
        return false;
    }

    private static CaptureFreshnessStamp PlaceStamp(int nonce, int x, int y, int w, int h, string name, List<CaptureFreshnessStamp> existing)
    {
        foreach (var shared in existing)
            if (shared.X >= x && shared.Y >= y && shared.X + shared.Width <= x + w && shared.Y + shared.Height <= y + h)
                return shared;
        // Try every fitting layout. An unobstructed rectangle can slide to the crop or a prior stamp's edge.
        for (int columns = Math.Min(w / CaptureFreshnessStamp.CellSize, CaptureFreshnessStamp.Cells); columns > 0; columns--)
        {
            var s = new CaptureFreshnessStamp(nonce, x, y, columns);
            if (s.Height > h) break;
            foreach (var left in existing.Select(e => e.X + e.Width).Prepend(x))
            foreach (var bottom in existing.Select(e => e.Y).Prepend(y + h))
            {
                var top = bottom - s.Height;
                if (left < x || left + s.Width > x + w || top < y || bottom > y + h) continue;
                if (existing.All(e => left >= e.X + e.Width || left + s.Width <= e.X || top >= e.Y + e.Height || bottom <= e.Y))
                    return s with { X = left, Y = top };
            }
        }
        throw new InvalidOperationException($"Popup '{name}' has insufficient space for a readable freshness stamp.");
    }

    private static void DrawStamp(ImGuiViewportPtr viewport, CaptureFreshnessStamp stamp)
    {
        var draw = ImGui.GetForegroundDrawList(viewport);
        for (var i = 0; i < CaptureFreshnessStamp.Cells; i++)
        {
            var min = viewport.Pos + new Vector2(stamp.CellX(i), stamp.CellY(i));
            draw.AddRectFilled(min, min + new Vector2(CaptureFreshnessStamp.CellSize), stamp.White(i) ? 0xffffffffu : 0xff000000u);
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
