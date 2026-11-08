using Franthropy.Dalamud.AgentBridge;
using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudAgentBridge;

/// <summary>
/// Captures the exact leased platform window outside the game renderer, then restores presentation.
/// </summary>
public sealed class PluginSurfaceCaptureService
{
    private readonly AgentBridgeClient client;
    private readonly NamedPipeBridgeClient pipe;
    private readonly WindowsGraphicsCaptureService capture;
    private readonly ReviewVault reviewVault;

    public PluginSurfaceCaptureService(AgentBridgeClient client, NamedPipeBridgeClient pipe, WindowsGraphicsCaptureService capture, ReviewVault reviewVault)
    {
        this.client = client;
        this.capture = capture;
        this.pipe = pipe;
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
            var connector = new BridgeTargetSelector("DalamudAgentBridge", profile, processId);
            captured = await CapturePresentedSurfaceAsync(
                connector,
                presentation.TransactionId,
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
        {
            reviewVault.Delete(captured.Review.Id);
            throw new InvalidOperationException(restoration?.Message ?? "Plugin surface restoration did not return a result.");
        }
        return new PluginSurfaceCaptureReviewReceipt(presentation, captured, restoration);
    }

    public async Task<PluginCaptureReviewReceipt> CapturePresentedSurfaceAsync(
        BridgeTargetSelector connector,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var instance = client.Resolve(connector);
        var measurement = await MeasureAsync(instance, transactionId, cancellationToken).ConfigureAwait(false);
        var captured = await capture.CaptureWindowAsync(instance.ProcessId, measurement, cancellationToken).ConfigureAwait(false);
        try
        {
            var after = await MeasureAsync(instance, transactionId, cancellationToken).ConfigureAwait(false);
            if (!measurement.HasSameTargetAndGeometry(after))
                throw new InvalidOperationException("The leased plugin window changed during capture; no review was published.");
            var receipt = new BridgeCaptureReceipt
            {
                SchemaVersion = 1,
                CaptureId = Guid.NewGuid().ToString("N"),
                FileName = "platform-window-memory",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Width = captured.Width, Height = captured.Height,
                Sha256 = Convert.ToHexString(SHA256.HashData(captured.PngBytes)),
                ProcessId = instance.ProcessId,
                Scope = "PluginSurface", CaptureMethod = "PrintWindowRenderFullContent",
                TargetPlugin = measurement.TargetPlugin, TransactionId = transactionId,
                ViewportId = measurement.ViewportId, FrameId = measurement.CompletedFrame,
            };
            var review = reviewVault.Store(receipt, captured.PngBytes);
            return new(AgentBridgeClient.ToView(instance), receipt, review, $"/api/reviews/{review.Id}.png");
        }
        finally { CryptographicOperations.ZeroMemory(captured.PngBytes); }
    }

    internal void DeleteReview(PluginCaptureReviewReceipt captured) => reviewVault.Delete(captured.Review.Id);

    private async Task<PluginSurfaceWindowMeasurement> MeasureAsync(BridgeInstance instance, string transactionId, CancellationToken cancellationToken)
    {
        var response = await pipe.SendAsync(instance, "measure-plugin-surface-window",
            new BridgeCommandRequest { TransactionId = transactionId }, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException($"Cannot measure the leased plugin window. {response.Message}");
        var measured = receipt.Deserialize<PluginSurfaceWindowMeasurement>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Plugin window measurement was empty.");
        if (measured.TransactionId != transactionId || string.IsNullOrWhiteSpace(measured.RuntimeInstanceId) ||
            string.IsNullOrWhiteSpace(measured.TargetPlugin) || string.IsNullOrWhiteSpace(measured.SurfaceId) ||
            measured.ViewportId == 0 || measured.CompletedFrame < 0)
            throw new InvalidDataException("Plugin window measurement did not identify the active lease.");
        return measured;
    }
}
