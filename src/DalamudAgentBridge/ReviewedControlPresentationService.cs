using System.Text.Json;
using System.Collections.Concurrent;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

public sealed class ReviewedControlPresentationService
{
    private readonly Func<BridgeInstance, string, BridgeCommandRequest?, CancellationToken, Task<PluginBridgeResponse>> send;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, AgentBridgeReviewSurfaceDescriptor[]> surfacesByPluginInstance = new(StringComparer.Ordinal);

    public ReviewedControlPresentationService(NamedPipeBridgeClient bridgeClient)
        : this(bridgeClient.SendAsync)
    {
    }

    internal ReviewedControlPresentationService(
        Func<BridgeInstance, string, BridgeCommandRequest?, CancellationToken, Task<PluginBridgeResponse>> send)
    {
        this.send = send;
    }

    public async Task<ReviewedControlPresentationReceipt> PresentAsync(
        BridgeInstance instance,
        ReviewedControlPresentationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurfaceId);
        var controlIds = request.ControlIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (controlIds.Length is < 1 or > 16)
            throw new ArgumentException("Between one and sixteen distinct control IDs are required.", nameof(request));
        if (controlIds.Any(id => id.Length > 256))
            throw new ArgumentException("Control IDs may not exceed 256 characters.", nameof(request));

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMilliseconds ?? 3_000, 250, 10_000));
        using var presentationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        presentationTimeout.CancelAfter(timeout);

        var surfaces = await GetSurfacesAsync(instance, presentationTimeout.Token).ConfigureAwait(false);
        var surface = surfaces.SingleOrDefault(value => string.Equals(value.Id, request.SurfaceId, StringComparison.Ordinal));
        if (surface == null)
        {
            surfacesByPluginInstance.TryRemove(SurfaceCacheKey(instance), out _);
            surfaces = await GetSurfacesAsync(instance, presentationTimeout.Token).ConfigureAwait(false);
            surface = surfaces.SingleOrDefault(value => string.Equals(value.Id, request.SurfaceId, StringComparison.Ordinal));
        }
        if (surface == null)
            throw new InvalidOperationException($"Review surface {request.SurfaceId} is not advertised by {instance.PluginName}.");

        var current = await TryReviewControlsAsync(instance, surface, controlIds, presentationTimeout.Token).ConfigureAwait(false);
        if (current != null)
            return current;

        var opened = await send(instance, "open-main-window", null, presentationTimeout.Token).ConfigureAwait(false);
        if (!opened.Success)
            throw new InvalidOperationException($"Could not open {instance.PluginName}: {opened.Message}");
        var selected = await send(instance, surface.Command, new BridgeCommandRequest { Target = surface.Target }, presentationTimeout.Token).ConfigureAwait(false);
        if (!selected.Success)
            throw new InvalidOperationException($"Could not present {surface.Label}: {selected.Message}");

        while (true)
        {
            presentationTimeout.Token.ThrowIfCancellationRequested();
            current = await TryReviewControlsAsync(instance, surface, controlIds, presentationTimeout.Token).ConfigureAwait(false);
            if (current != null)
                return current;
            await Task.Delay(25, presentationTimeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<AgentBridgeReviewSurfaceDescriptor[]> GetSurfacesAsync(
        BridgeInstance instance,
        CancellationToken cancellationToken)
    {
        var key = SurfaceCacheKey(instance);
        if (surfacesByPluginInstance.TryGetValue(key, out var cached))
            return cached;
        var response = await send(instance, "get-review-surfaces", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } element)
            throw new InvalidOperationException($"Review-surface discovery failed: {response.Message}");
        var surfaces = element.Deserialize<AgentBridgeReviewSurfaceDescriptor[]>(jsonOptions) ?? [];
        surfacesByPluginInstance[key] = surfaces;
        return surfaces;
    }

    private async Task<ReviewedControlPresentationReceipt?> TryReviewControlsAsync(
        BridgeInstance instance,
        AgentBridgeReviewSurfaceDescriptor surface,
        IReadOnlyList<string> controlIds,
        CancellationToken cancellationToken)
    {
        var reviews = new List<AgentBridgeUiControlReview>(controlIds.Count);
        foreach (var controlId in controlIds)
        {
            var response = await send(instance, "get-control", new BridgeCommandRequest { Target = controlId }, cancellationToken).ConfigureAwait(false);
            if (response.Receipt is not { } reviewElement)
                continue;
            var review = reviewElement.Deserialize<AgentBridgeUiControlReview>(jsonOptions);
            if (response.Success && review?.Control != null)
                reviews.Add(review);
        }
        if (reviews.Count != controlIds.Count || reviews.Select(review => review.FrameId).Distinct().Count() != 1 ||
            reviews[0].ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return null;
        var first = reviews[0];
        return new ReviewedControlPresentationReceipt(
            surface.Id,
            surface.Label,
            first.FrameId,
            first.RenderedAtUtc,
            first.ExpiresAtUtc,
            reviews.Select(review => review.Control!).ToArray());
    }

    private static string SurfaceCacheKey(BridgeInstance instance) => $"{instance.Id}\n{instance.PluginInstanceId}";

    public async Task<ReviewedControlActionReceipt> PresentAndInvokeAsync(
        BridgeInstance instance,
        ReviewedControlActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ControlId);
        var presentation = await PresentAsync(instance, new ReviewedControlPresentationRequest
        {
            SurfaceId = request.SurfaceId,
            ControlIds = [request.ControlId],
            TimeoutMilliseconds = request.TimeoutMilliseconds,
        }, cancellationToken).ConfigureAwait(false);
        var control = presentation.Controls.Single();
        if (!control.Enabled)
            throw new InvalidOperationException($"Reviewed control {request.ControlId} is disabled: {control.Value}");

        var invocation = await send(instance, "invoke-control", new BridgeCommandRequest
        {
            Target = request.ControlId,
            FrameId = presentation.FrameId,
            Arguments = request.Arguments,
        }, cancellationToken).ConfigureAwait(false);
        if (!invocation.Success)
            throw new InvalidOperationException($"Reviewed control invocation failed: {invocation.Message}");
        return new ReviewedControlActionReceipt(presentation, invocation);
    }
}
