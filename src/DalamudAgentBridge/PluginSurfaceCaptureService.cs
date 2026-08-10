using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

/// <summary>
/// Presents one reflected window under a lease, captures the full rendered viewport, and restores
/// prior state in a finally path. This is the useful composite operation; callers never coordinate
/// reflected state themselves.
/// </summary>
public sealed class PluginSurfaceCaptureService
{
    private readonly AgentBridgeClient client;
    private readonly PluginCaptureService capture;

    public PluginSurfaceCaptureService(AgentBridgeClient client, PluginCaptureService capture)
    {
        this.client = client;
        this.capture = capture;
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
            throw new InvalidOperationException(restoration?.Message ?? "Plugin surface restoration did not return a result.");
        return new PluginSurfaceCaptureReviewReceipt(presentation, captured, restoration);
    }

    // Transient capture-readiness markers. These must stay in sync with the
    // in-game capture failure messages in DalamudAgentBridge.Plugin's
    // AgentBridgeViewportCaptureService (bounds lease, zero-size, freshness,
    // viewport readback readiness). Retry applies only while the presented
    // surface settles; genuine failures surface after the deadline.
    private static readonly string[] TransientCaptureMarkers = ["capture bounds", "capture viewport"];

    public async Task<PluginCaptureReviewReceipt> CapturePresentedSurfaceAsync(
        BridgeTargetSelector connector,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                return await capture.CaptureAsync(
                    connector,
                    new BridgeCommandRequest { TransactionId = transactionId },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (
                TransientCaptureMarkers.Any(marker => exception.Message.Contains(marker, StringComparison.OrdinalIgnoreCase)) &&
                DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
