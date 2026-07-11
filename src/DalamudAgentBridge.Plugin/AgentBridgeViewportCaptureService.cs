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

public sealed class AgentBridgeViewportCaptureService
{
    private readonly string captureDirectory;
    private readonly string pluginInstanceId;
    private readonly Func<AgentBridgeViewportRegion?> captureRegion;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly ITextureProvider textureProvider;
    private readonly ITextureReadbackProvider readbackProvider;
    private readonly SemaphoreSlim captureLock = new(1, 1);

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
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var region = captureRegion();
            if (!fullViewport && (region == null || !region.IsFresh(TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow)))
                throw new InvalidOperationException("The Agent Bridge window is not currently rendered; no screenshot was captured.");

            Task<IDalamudTextureWrap>? textureTask = null;
            await dispatchOnFramework(() =>
            {
                var currentRegion = captureRegion();
                if (!fullViewport && (currentRegion == null || !currentRegion.IsFresh(TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow)))
                    throw new InvalidOperationException("The Agent Bridge window is not currently rendered; no screenshot was captured.");
                textureTask = textureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
                {
                    ViewportId = ImGui.GetMainViewport().ID,
                    AutoUpdate = false,
                    TakeBeforeImGuiRender = false,
                    KeepTransparency = false,
                    Uv0 = fullViewport ? default : currentRegion!.GetUvBounds().Uv0,
                    Uv1 = fullViewport ? default : currentRegion!.GetUvBounds().Uv1,
                }, "Dalamud Agent Bridge viewport capture", cancellationToken);
            }).ConfigureAwait(false);

            using var texture = await (textureTask ?? throw new InvalidOperationException("Viewport capture was not scheduled.")).ConfigureAwait(false);
            var pngCodec = readbackProvider.GetSupportedImageEncoderInfos().Single(codec => codec.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase));
            await using var output = new MemoryStream();
            await readbackProvider.SaveToStreamAsync(texture, pngCodec.ContainerGuid, output, new Dictionary<string, object>(), true, true, cancellationToken).ConfigureAwait(false);

            var pngBytes = output.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(pngBytes));
            var protectedBytes = AgentBridgeDataProtection.ProtectBytes(pngBytes, pluginInstanceId);
            CryptographicOperations.ZeroMemory(pngBytes);
            var captureId = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(captureDirectory);
            var path = Path.Combine(captureDirectory, $"{captureId}.bin");
            await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(protectedBytes);
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
                Scope = fullViewport ? "FullViewport" : "BridgeWindow",
            };
        }
        finally { captureLock.Release(); }
    }
}
