using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

public sealed class AgentBridgeViewportCaptureService : IDisposable
{
    private readonly string captureDirectory;
    private readonly string pluginInstanceId;
    private readonly Func<AgentBridgeViewportRegion?> captureRegion;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly ITextureProvider textureProvider;
    private readonly ITextureReadbackProvider readbackProvider;
    private readonly SemaphoreSlim captureLock = new(1, 1);
    private readonly object pendingWindowGate = new();
    private PendingWindowCapture? pendingWindowCapture;

    public AgentBridgeViewportCaptureService(
        string configDirectory,
        string pluginInstanceId,
        Func<AgentBridgeViewportRegion?> captureRegion,
        Func<Action, Task> dispatchOnFramework,
        ITextureProvider textureProvider,
        ITextureReadbackProvider readbackProvider)
    {
        this.pluginInstanceId = pluginInstanceId;
        this.captureRegion = captureRegion;
        this.dispatchOnFramework = dispatchOnFramework;
        this.textureProvider = textureProvider;
        this.readbackProvider = readbackProvider;
        captureDirectory = Path.Combine(configDirectory, "agent-bridge", "captures");
    }

    public async Task<AgentBridgeCaptureReceipt> CaptureAsync(bool fullViewport, CancellationToken cancellationToken)
        => await CaptureAsync(
            fullViewport,
            captureRegion,
            fullViewport ? "FullViewport" : "BridgeWindow",
            cancellationToken).ConfigureAwait(false);

    public async Task<AgentBridgeCaptureReceipt> CaptureRegionAsync(
        Func<AgentBridgeViewportRegion?> region,
        string scope,
        CancellationToken cancellationToken)
        => await CaptureAsync(false, region, scope, cancellationToken).ConfigureAwait(false);

    public async Task<AgentBridgeCaptureReceipt> CaptureWindowAsync(
        Func<string> windowName,
        string scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windowName);
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new PendingWindowCapture(
                windowName,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (pendingWindowGate)
            {
                if (pendingWindowCapture is not null)
                    throw new InvalidOperationException("A plugin window capture is already waiting for an ImGui draw frame.");
                pendingWindowCapture = pending;
            }

            try
            {
                var measurement = await pending.Completion.Task
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
                using var texture = await CreateViewportRegionTextureAsync(
                    measurement.Region,
                    "Dalamud Agent Bridge plugin window capture",
                    cancellationToken).ConfigureAwait(false);
                return await PersistCaptureAsync(
                    texture,
                    scope,
                    "ImGuiViewportRegion",
                    measurement.Region.ViewportId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "Plugin surface capture bounds were not rendered during the two-second ImGui draw lease.",
                    exception);
            }
            finally
            {
                lock (pendingWindowGate)
                {
                    if (ReferenceEquals(pendingWindowCapture, pending))
                        pendingWindowCapture = null;
                }
            }
        }
        finally { captureLock.Release(); }
    }

    public async Task<AgentBridgePluginWindowMeasurement> MeasureWindowAsync(
        Func<string> windowName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windowName);
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = new PendingWindowCapture(
                windowName,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (pendingWindowGate)
            {
                if (pendingWindowCapture is not null)
                    throw new InvalidOperationException("A plugin window measurement is already waiting for an ImGui draw frame.");
                pendingWindowCapture = pending;
            }

            try
            {
                var measurement = await pending.Completion.Task
                    .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
                return new(
                    measurement.WindowHandle,
                    (int)MathF.Round(measurement.Region.WindowPosition.X - measurement.Region.ViewportPosition.X),
                    (int)MathF.Round(measurement.Region.WindowPosition.Y - measurement.Region.ViewportPosition.Y),
                    (int)MathF.Round(measurement.Region.WindowSize.X),
                    (int)MathF.Round(measurement.Region.WindowSize.Y));
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "Plugin surface window ownership was not rendered during the two-second ImGui draw lease.",
                    exception);
            }
            finally
            {
                lock (pendingWindowGate)
                {
                    if (ReferenceEquals(pendingWindowCapture, pending))
                        pendingWindowCapture = null;
                }
            }
        }
        finally { captureLock.Release(); }
    }

    public unsafe void RenderPendingWindowCapture()
    {
        PendingWindowCapture? pending;
        lock (pendingWindowGate)
            pending = pendingWindowCapture;
        if (pending is null || pending.Completion.Task.IsCompleted)
            return;

        try
        {
            var name = pending.WindowName();
            var window = ImGuiP.FindWindowByName(new ImU8String(name));
            if (window.IsNull || (!window.Active && !window.WasActive) || window.Hidden)
                return;
            if (window.Size.X <= 0f || window.Size.Y <= 0f)
                throw new InvalidOperationException($"Plugin surface capture bounds are unavailable because '{name}' rendered with zero size.");

            var viewport = window.ViewportId == 0
                ? ImGui.GetMainViewport()
                : ImGui.FindViewportByID(window.ViewportId);
            if (viewport.IsNull)
                throw new InvalidOperationException($"Plugin surface capture viewport 0x{window.ViewportId:X8} is unavailable.");
            var windowHandle = (nint)viewport.PlatformHandleRaw;
            if (windowHandle == nint.Zero)
                throw new InvalidOperationException($"Plugin surface capture viewport 0x{viewport.ID:X8} has no platform window handle.");

            pending.Completion.TrySetResult(new(
                windowHandle,
                new(
                    window.Pos,
                    window.Size,
                    viewport.Pos,
                    viewport.Size,
                    DateTimeOffset.UtcNow)
                {
                    ViewportId = viewport.ID,
                }));
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    public void Dispose()
    {
        lock (pendingWindowGate)
        {
            pendingWindowCapture?.Completion.TrySetCanceled();
            pendingWindowCapture = null;
        }
    }

    private async Task<AgentBridgeCaptureReceipt> CaptureAsync(
        bool fullViewport,
        Func<AgentBridgeViewportRegion?> regionProvider,
        string scope,
        CancellationToken cancellationToken)
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task<IDalamudTextureWrap>? textureTask = null;
            uint? capturedViewportId = null;
            await dispatchOnFramework(() =>
            {
                var currentRegion = regionProvider();
                if (!fullViewport && (currentRegion == null || !currentRegion.IsFresh(TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow)))
                    throw new InvalidOperationException("The requested rendered surface has no fresh capture bounds.");
                capturedViewportId = fullViewport
                    ? ImGui.GetMainViewport().ID
                    : currentRegion!.ViewportId ?? ImGui.GetMainViewport().ID;
                var uvBounds = fullViewport ? default : currentRegion!.GetUvBounds();
                textureTask = textureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
                {
                    ViewportId = capturedViewportId.Value,
                    AutoUpdate = false,
                    TakeBeforeImGuiRender = false,
                    KeepTransparency = false,
                    Uv0 = fullViewport ? default : uvBounds.Uv0,
                    Uv1 = fullViewport ? default : uvBounds.Uv1,
                }, "Dalamud Agent Bridge viewport capture", cancellationToken);
            }).ConfigureAwait(false);

            IDalamudTextureWrap texture;
            try
            {
                texture = await (textureTask ?? throw new InvalidOperationException("Viewport capture was not scheduled.")).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                !fullViewport &&
                exception is InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException(
                    $"Plugin surface capture viewport 0x{capturedViewportId:X8} is not ready for texture readback.",
                    exception);
            }
            using var capturedTexture = texture;
            return await PersistCaptureAsync(
                capturedTexture,
                scope,
                "ImGuiViewport",
                capturedViewportId,
                cancellationToken).ConfigureAwait(false);
        }
        finally { captureLock.Release(); }
    }

    private async Task<AgentBridgeCaptureReceipt> PersistCaptureAsync(
        IDalamudTextureWrap texture,
        string scope,
        string captureMethod,
        uint? viewportId,
        CancellationToken cancellationToken)
    {
        var pngCodec = readbackProvider.GetSupportedImageEncoderInfos().Single(codec => codec.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase));
        await using var output = new MemoryStream();
        await readbackProvider.SaveToStreamAsync(texture, pngCodec.ContainerGuid, output, new Dictionary<string, object>(), true, true, cancellationToken).ConfigureAwait(false);

        var pngBytes = output.ToArray();
        try
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(pngBytes));
            var protectedBytes = AgentBridgeDataProtection.ProtectBytes(pngBytes, pluginInstanceId);
            try
            {
                var captureId = Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(captureDirectory);
                var path = Path.Combine(captureDirectory, $"{captureId}.bin");
                await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
                return new AgentBridgeCaptureReceipt
                {
                    SchemaVersion = 1,
                    CaptureId = captureId,
                    FileName = $"{captureId}.bin",
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Width = texture.Width,
                    Height = texture.Height,
                    Sha256 = sha256,
                    ProcessId = Environment.ProcessId,
                    Scope = scope,
                    CaptureMethod = captureMethod,
                    ViewportId = viewportId,
                };
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(pngBytes); }
    }

    private async Task<IDalamudTextureWrap> CreateViewportRegionTextureAsync(
        AgentBridgeViewportRegion region,
        string debugName,
        CancellationToken cancellationToken)
    {
        Task<IDalamudTextureWrap>? textureTask = null;
        await dispatchOnFramework(() =>
        {
            if (!region.IsFresh(TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow))
                throw new InvalidOperationException("The requested rendered plugin window has no fresh capture bounds.");
            var viewportId = region.ViewportId ?? ImGui.GetMainViewport().ID;
            var uvBounds = region.GetUvBounds(0f);
            textureTask = textureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
            {
                ViewportId = viewportId,
                AutoUpdate = false,
                TakeBeforeImGuiRender = false,
                KeepTransparency = false,
                Uv0 = uvBounds.Uv0,
                Uv1 = uvBounds.Uv1,
            }, debugName, cancellationToken);
        }).ConfigureAwait(false);

        try
        {
            return await (textureTask ?? throw new InvalidOperationException("Plugin window capture was not scheduled.")).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Plugin surface capture viewport 0x{region.ViewportId:X8} is not ready for texture readback.",
                exception);
        }
    }

    private sealed record PendingWindowCapture(
        Func<string> WindowName,
        TaskCompletionSource<PluginWindowMeasurement> Completion);

    private sealed record PluginWindowMeasurement(
        nint WindowHandle,
        AgentBridgeViewportRegion Region);
}

public sealed record AgentBridgePluginWindowMeasurement(
    long WindowHandle,
    int X,
    int Y,
    int Width,
    int Height);
