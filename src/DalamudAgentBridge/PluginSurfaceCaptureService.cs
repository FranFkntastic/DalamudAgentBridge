using Franthropy.Dalamud.AgentBridge;
using System.Security.Cryptography;

namespace DalamudAgentBridge;

/// <summary>
/// Presents one reflected window under a lease, captures the final compositor output outside
/// the game process, and restores prior state in a finally path.
/// </summary>
public sealed class PluginSurfaceCaptureService
{
    private readonly AgentBridgeClient client;
    private readonly WindowsGraphicsCaptureService capture;
    private readonly ReviewVault reviewVault;

    public PluginSurfaceCaptureService(
        AgentBridgeClient client,
        WindowsGraphicsCaptureService capture,
        ReviewVault reviewVault)
    {
        this.client = client;
        this.capture = capture;
        this.reviewVault = reviewVault;
    }

    public async Task<PluginSurfaceCaptureReviewReceipt> CaptureAsync(
        string plugin,
        string surfaceId,
        string? profile,
        int? processId,
        CancellationToken cancellationToken)
    {
        var presentation = await client.BeginPluginSurfacePresentationAsync(
            plugin,
            surfaceId,
            profile,
            processId,
            cancellationToken).ConfigureAwait(false);
        PluginCaptureReviewReceipt? captured = null;
        AgentBridgePluginSurfacePresentationResult? restoration = null;
        try
        {
            captured = await CapturePresentedSurfaceAsync(
                new BridgeTargetSelector("DalamudAgentBridge", profile, processId),
                presentation.TransactionId,
                plugin,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                restoration = await client.RestorePluginSurfacePresentationAsync(
                    presentation.TransactionId,
                    profile,
                    processId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                restoration = new AgentBridgePluginSurfacePresentationResult(
                    false,
                    $"Automatic restoration failed: {exception.Message}",
                    presentation.TransactionId);
            }
        }

        if (captured is null)
            throw new InvalidOperationException("Plugin surface capture did not return a review.");
        if (restoration is not { Success: true })
            throw new InvalidOperationException(restoration?.Message ?? "Plugin surface restoration did not return a result.");
        return new PluginSurfaceCaptureReviewReceipt(presentation, captured, restoration);
    }

    public async Task<PluginCaptureReviewReceipt> CapturePresentedSurfaceAsync(
        BridgeTargetSelector connector,
        string transactionId,
        string? targetPlugin,
        CancellationToken cancellationToken)
    {
        var instance = client.Resolve(connector);
        // Presentation changes IWindow state synchronously; pixels arrive on the next ImGui
        // pass. WGC then reads the final compositor output from outside FFXIV, so a target
        // Draw() exception can fail a review without scheduling a fatal post-render texture
        // callback inside Dalamud.
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        byte[]? pngBytes = null;
        try
        {
            var captured = await capture.CaptureAsync(instance.ProcessId, cancellationToken).ConfigureAwait(false);
            pngBytes = captured.PngBytes;
            var receipt = new BridgeCaptureReceipt
            {
                SchemaVersion = 1,
                CaptureId = Guid.NewGuid().ToString("N"),
                FileName = "windows-graphics-capture-memory",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Width = captured.Width,
                Height = captured.Height,
                Sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)),
                ProcessId = instance.ProcessId,
                Scope = "PluginSurface",
                CaptureMethod = "WindowsGraphicsCapture",
                TargetPlugin = targetPlugin,
                TransactionId = transactionId,
            };
            var review = reviewVault.Store(receipt, pngBytes);
            return new PluginCaptureReviewReceipt(
                AgentBridgeClient.ToView(instance),
                receipt,
                review,
                $"/api/reviews/{review.Id}.png");
        }
        finally
        {
            if (pngBytes is not null)
                CryptographicOperations.ZeroMemory(pngBytes);
        }
    }
}
