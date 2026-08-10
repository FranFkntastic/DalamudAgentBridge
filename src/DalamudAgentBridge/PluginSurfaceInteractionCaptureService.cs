using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

/// <summary>
/// Presents a reflected plugin window, executes bounded ImGui-only input, captures the settled
/// result, and restores the prior window state in one transaction-shaped operation.
/// </summary>
public sealed class PluginSurfaceInteractionCaptureService
{
    private readonly AgentBridgeClient client;
    private readonly PluginSurfaceCaptureService capture;

    public PluginSurfaceInteractionCaptureService(
        AgentBridgeClient client,
        PluginSurfaceCaptureService capture)
    {
        this.client = client;
        this.capture = capture;
    }

    public async Task<PluginSurfaceInteractionCaptureReviewReceipt> InteractAndCaptureAsync(
        string plugin,
        string surfaceId,
        PluginSurfaceInputSequenceRequest sequence,
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
        PluginSurfaceInputReceipt? interaction = null;
        PluginCaptureReviewReceipt? captured = null;
        AgentBridgePluginSurfacePresentationResult? restoration = null;
        try
        {
            interaction = await client.InteractPluginSurfaceAsync(
                presentation.TransactionId,
                sequence,
                profile,
                processId,
                cancellationToken).ConfigureAwait(false);
            captured = await capture.CapturePresentedSurfaceAsync(
                new BridgeTargetSelector("DalamudAgentBridge", profile, processId),
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
                restoration = new(
                    false,
                    $"Automatic restoration failed: {exception.Message}",
                    presentation.TransactionId);
            }
        }

        if (interaction is null)
            throw new InvalidOperationException("Plugin surface input did not return a receipt.");
        if (captured is null)
            throw new InvalidOperationException("Plugin surface interaction did not return a review capture.");
        if (restoration is not { Success: true })
            throw new InvalidOperationException(restoration?.Message ?? "Plugin surface restoration did not return a result.");
        return new(presentation, interaction, captured, restoration);
    }
}
