using System.Text.Json;
using System.Collections.Concurrent;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

public sealed class ReviewedControlPresentationService
{
    private readonly Func<BridgeInstance, string, BridgeCommandRequest?, CancellationToken, Task<PluginBridgeResponse>> send;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, AgentBridgeReviewSurfaceDescriptor[]> surfacesByCatalog = new(StringComparer.Ordinal);

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
            foreach (var key in surfacesByCatalog.Keys.Where(key => key.StartsWith(RuntimeCachePrefix(instance), StringComparison.Ordinal)))
                surfacesByCatalog.TryRemove(key, out _);
            surfaces = await GetSurfacesAsync(instance, presentationTimeout.Token).ConfigureAwait(false);
            surface = surfaces.SingleOrDefault(value => string.Equals(value.Id, request.SurfaceId, StringComparison.Ordinal));
        }
        if (surface == null)
            throw new InvalidOperationException($"Review surface {request.SurfaceId} is not advertised by {instance.PluginName}.");

        var probe = await ProbeControlsAsync(instance, surface, controlIds, presentationTimeout.Token).ConfigureAwait(false);
        if (probe.Receipt != null)
            return probe.Receipt;

        var opened = await send(instance, "open-main-window", null, presentationTimeout.Token).ConfigureAwait(false);
        if (!opened.Success)
            throw new InvalidOperationException($"Could not open {instance.PluginName}: {opened.Message}");
        var selected = await send(instance, surface.Command, new BridgeCommandRequest { Target = surface.Target }, presentationTimeout.Token).ConfigureAwait(false);
        if (!selected.Success)
            throw new InvalidOperationException($"Could not present {surface.Label}: {selected.Message}");

        try
        {
            while (true)
            {
                presentationTimeout.Token.ThrowIfCancellationRequested();
                probe = await ProbeControlsAsync(instance, surface, controlIds, presentationTimeout.Token).ConfigureAwait(false);
                if (probe.Receipt != null)
                    return probe.Receipt;
                await Task.Delay(25, presentationTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(CreatePresentationTimeoutDiagnostic(surface, controlIds, probe));
        }
    }

    private async Task<AgentBridgeReviewSurfaceDescriptor[]> GetSurfacesAsync(
        BridgeInstance instance,
        CancellationToken cancellationToken)
    {
        var manifestResponse = await send(instance, "get-manifest", null, cancellationToken).ConfigureAwait(false);
        if (manifestResponse.Success && manifestResponse.Receipt is { } manifestElement)
        {
            var manifest = manifestElement.Deserialize<AgentBridgeManifest>(jsonOptions);
            if (manifest is not null)
            {
                var manifestKey = $"{RuntimeCachePrefix(instance)}\n{manifest.CatalogRevision}";
                foreach (var staleKey in surfacesByCatalog.Keys.Where(candidate => candidate.StartsWith(RuntimeCachePrefix(instance), StringComparison.Ordinal) && candidate != manifestKey))
                    surfacesByCatalog.TryRemove(staleKey, out _);
                return surfacesByCatalog.GetOrAdd(manifestKey, _ => manifest.ReviewSurfaces.ToArray());
            }
        }

        var key = $"{RuntimeCachePrefix(instance)}\nlegacy";
        if (surfacesByCatalog.TryGetValue(key, out var cached))
            return cached;
        var response = await send(instance, "get-review-surfaces", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } element)
            throw new InvalidOperationException($"Review-surface discovery failed: {response.Message}");
        var surfaces = element.Deserialize<AgentBridgeReviewSurfaceDescriptor[]>(jsonOptions) ?? [];
        surfacesByCatalog[key] = surfaces;
        return surfaces;
    }

    private async Task<ControlReviewProbe> ProbeControlsAsync(
        BridgeInstance instance,
        AgentBridgeReviewSurfaceDescriptor surface,
        IReadOnlyList<string> controlIds,
        CancellationToken cancellationToken)
    {
        var reviews = new List<AgentBridgeUiControlReview>(controlIds.Count);
        foreach (var controlId in controlIds)
        {
            var request = new BridgeCommandRequest { Target = controlId };
            var response = await send(instance, "get-control", request, cancellationToken).ConfigureAwait(false);
            if (!response.Success && response.Receipt is null &&
                response.Message.Contains("command is not allowed", StringComparison.OrdinalIgnoreCase))
            {
                response = await send(instance, "review-control", request, cancellationToken).ConfigureAwait(false);
            }
            if (response.Receipt is not { } reviewElement)
                continue;
            var review = reviewElement.Deserialize<AgentBridgeUiControlReview>(jsonOptions);
            if (response.Success && review?.Control != null)
                reviews.Add(review);
        }
        if (reviews.Count != controlIds.Count || reviews.Select(review => review.FrameId).Distinct().Count() != 1 ||
            reviews[0].ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return new(null, reviews.OrderByDescending(review => review.RenderedAtUtc).FirstOrDefault(), reviews.Count);
        }
        var first = reviews[0];
        return new(
            new ReviewedControlPresentationReceipt(
                surface.Id,
                surface.Label,
                first.FrameId,
                first.RenderedAtUtc,
                first.ExpiresAtUtc,
                reviews.Select(review => review.Control!).ToArray()),
            first,
            reviews.Count);
    }

    private static string CreatePresentationTimeoutDiagnostic(
        AgentBridgeReviewSurfaceDescriptor surface,
        IReadOnlyList<string> controlIds,
        ControlReviewProbe probe)
    {
        var requested = string.Join(", ", controlIds);
        if (probe.LatestReview is null)
        {
            return $"{surface.Label} opened, but the plugin produced no rendered control review for {requested}. " +
                "The window may be collapsed, or the requested view did not render.";
        }

        var review = probe.LatestReview;
        if (review.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return $"{surface.Label} opened, but rendered frame {review.FrameId} from {review.RenderedAtUtc:O} " +
                $"expired without advancing ({probe.FoundControlCount}/{controlIds.Count} requested controls found). " +
                "The window is likely collapsed, or the requested view stopped rendering.";
        }

        return $"{surface.Label} rendered frame {review.FrameId}, but only {probe.FoundControlCount}/{controlIds.Count} " +
            $"requested controls appeared before timeout: {requested}.";
    }

    private sealed record ControlReviewProbe(
        ReviewedControlPresentationReceipt? Receipt,
        AgentBridgeUiControlReview? LatestReview,
        int FoundControlCount);

    private static string RuntimeCachePrefix(BridgeInstance instance) =>
        $"{instance.Id}\n{instance.RuntimeInstanceId ?? instance.PluginInstanceId}";

    public async Task<ReviewedControlActionReceipt> PresentAndInvokeAsync(
        BridgeInstance instance,
        ReviewedControlActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ControlId);
        var surfaceId = request.SurfaceId;
        if (string.IsNullOrWhiteSpace(surfaceId))
            surfaceId = await ResolveActionSurfaceAsync(instance, request.ControlId, cancellationToken).ConfigureAwait(false);
        var presentation = await PresentAsync(instance, new ReviewedControlPresentationRequest
        {
            SurfaceId = surfaceId,
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

    private async Task<string> ResolveActionSurfaceAsync(
        BridgeInstance instance,
        string controlId,
        CancellationToken cancellationToken)
    {
        var response = await send(instance, "get-manifest", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } element)
            throw new InvalidOperationException("A surface ID is required because this bridge does not advertise a usable action catalog.");
        var manifest = element.Deserialize<AgentBridgeManifest>(jsonOptions)
            ?? throw new InvalidOperationException("The bridge returned an empty action catalog.");
        var matches = manifest.Actions.Where(action => string.Equals(action.Id, controlId, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            1 => matches[0].SurfaceId,
            0 => throw new InvalidOperationException($"Action {controlId} is not advertised by {instance.PluginName}."),
            _ => throw new InvalidOperationException($"Action {controlId} is advertised on multiple surfaces; specify a surface ID."),
        };
    }
}
