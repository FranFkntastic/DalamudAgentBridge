using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

/// <summary>Discovers and resolves capture surfaces without knowing which plugin provides them.</summary>
public sealed class CaptureSurfaceDiscoveryService
{
    private readonly Func<BridgeInstance, string, BridgeCommandRequest?, CancellationToken, Task<PluginBridgeResponse>> send;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CaptureSurfaceDiscoveryService(NamedPipeBridgeClient bridgeClient)
        : this(bridgeClient.SendAsync)
    {
    }

    internal CaptureSurfaceDiscoveryService(
        Func<BridgeInstance, string, BridgeCommandRequest?, CancellationToken, Task<PluginBridgeResponse>> send)
    {
        this.send = send;
    }

    public async Task<IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor>> GetAsync(
        BridgeInstance instance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var response = await send(instance, "get-capture-surfaces", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } element)
            throw new InvalidOperationException($"Capture-surface discovery failed: {response.Message}");

        var surfaces = (element.Deserialize<AgentBridgeCaptureSurfaceDescriptor[]>(jsonOptions) ?? [])
            .Where(surface => !string.IsNullOrWhiteSpace(surface.Id) && !string.IsNullOrWhiteSpace(surface.Label))
            .GroupBy(surface => surface.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(surface => surface.Order)
            .ToArray();
        if (surfaces.Length == 0)
            throw new InvalidOperationException($"{instance.PluginName} does not advertise any capture surfaces.");
        if (surfaces.Count(surface => surface.IsDefault) > 1)
            throw new InvalidOperationException($"{instance.PluginName} advertises more than one default capture surface.");
        return surfaces;
    }

    public async Task<AgentBridgeCaptureSurfaceDescriptor> ResolveAsync(
        BridgeInstance instance,
        string? requestedTarget,
        CancellationToken cancellationToken)
    {
        var surfaces = await GetAsync(instance, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestedTarget))
            return surfaces.SingleOrDefault(surface => surface.IsDefault) ?? surfaces[0];

        return surfaces.SingleOrDefault(surface => string.Equals(surface.Id, requestedTarget, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Capture surface {requestedTarget} is not advertised by {instance.PluginName}.");
    }
}
