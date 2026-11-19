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
        var manifest = await client.GetManifestAsync(instance, cancellationToken).ConfigureAwait(false);
        var popupCapable = manifest.Capabilities.Any(c => c.Id == PluginSurfaceWindowMeasurement.PopupCapability);
        var nonce = BitConverter.ToInt32(RandomNumberGenerator.GetBytes(sizeof(int)));
        var measurement = await MeasureAsync(instance, transactionId, nonce, cancellationToken).ConfigureAwait(false);
        if (popupCapable != (measurement.Attached != null))
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.InvalidFrame, "Popup measurement does not match connector capabilities.");
        var captured = await capture.CaptureWindowAsync(instance.ProcessId, measurement, cancellationToken).ConfigureAwait(false);
        try
        {
            var after = await MeasureAsync(instance, transactionId, nonce, cancellationToken).ConfigureAwait(false);
            if (!measurement.HasSameTargetAndGeometry(after))
                throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.InvalidFrame,
                    "The leased plugin window or its attached popup tree changed during capture; no review was published.");
            var layout = SurfaceCompositeLayout.From(measurement);
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
                Stamp = measurement.Stamp! with { X = layout.MainRect.X + measurement.Stamp.X, Y = layout.MainRect.Y + measurement.Stamp.Y },
                MainRect = layout.MainRect,
                AttachedRegions = layout.Attached,
                PopupCoverage = measurement.Attached == null ? "main-window-only (legacy connector)" : "attached-popup-tree",
            };
            var review = reviewVault.Store(receipt, captured.PngBytes);
            return new(AgentBridgeClient.ToView(instance), receipt, review, $"/api/reviews/{review.Id}.png");
        }
        finally { CryptographicOperations.ZeroMemory(captured.PngBytes); }
    }

    internal void DeleteReview(PluginCaptureReviewReceipt captured) => reviewVault.Delete(captured.Review.Id);

    private async Task<PluginSurfaceWindowMeasurement> MeasureAsync(BridgeInstance instance, string transactionId, int nonce, CancellationToken cancellationToken)
    {
        var response = await pipe.SendAsync(instance, "measure-plugin-surface-window",
            new BridgeCommandRequest { TransactionId = transactionId, Arguments = JsonSerializer.SerializeToElement(nonce) }, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.CaptureFailed,
                $"Cannot measure the leased plugin window. {response.Message}");
        var measured = receipt.Deserialize<PluginSurfaceWindowMeasurement>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Plugin window measurement was empty.");
        if (measured.Stamp is not { } stamp)
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.ConnectorTooOld,
                "Connector too old for verified capture; update DalamudAgentBridge plugin.");
        if (stamp.Nonce != nonce || stamp.CellsPerRow is < 1 or > CaptureFreshnessStamp.Cells || stamp.X < 0 || stamp.Y < 0 ||
            (long)stamp.X + stamp.Width > measured.Width || (long)stamp.Y + stamp.Height > measured.Height)
            throw new InvalidDataException("Plugin window measurement returned invalid freshness stamp data.");
        if (measured.TransactionId != transactionId || string.IsNullOrWhiteSpace(measured.RuntimeInstanceId) ||
            string.IsNullOrWhiteSpace(measured.TargetPlugin) || string.IsNullOrWhiteSpace(measured.SurfaceId) ||
            measured.ViewportId == 0 || measured.CompletedFrame < 0)
            throw new InvalidDataException("Plugin window measurement did not identify the active lease.");
        if (measured.Attached is { } attached &&
            (attached.Length > 8 || attached.Any(p => p == null || p.WindowId == 0 || p.ViewportId == 0 ||
                string.IsNullOrEmpty(p.Name) || p.ViewportStamp is not { } s || s.Nonce != nonce ||
                s.CellsPerRow is < 1 or > CaptureFreshnessStamp.Cells || s.X < 0 || s.Y < 0)))
            throw new WindowsGraphicsCaptureException(WindowsGraphicsCaptureFailure.InvalidFrame, "Invalid attached popup measurement or freshness stamp.");
        return measured;
    }
}
