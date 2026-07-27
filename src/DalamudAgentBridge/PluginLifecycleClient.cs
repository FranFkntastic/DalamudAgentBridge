using System.Text.Json;

namespace DalamudAgentBridge;

public interface IPluginLifecycleClient
{
    Task<InstalledPluginSnapshot> ListAsync(BridgeInstance instance, CancellationToken cancellationToken);
    Task<PluginBridgeResponse> SetEnabledAsync(BridgeInstance instance, string internalName, bool enabled, CancellationToken cancellationToken);
    Task<PluginBridgeResponse> InstallAsync(BridgeInstance instance, string internalName, CancellationToken cancellationToken);
    Task<PluginBridgeResponse> InstallDevAsync(BridgeInstance instance, string internalName, CancellationToken cancellationToken);
}

public sealed class PluginLifecycleClient : IPluginLifecycleClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly NamedPipeBridgeClient bridgeClient;

    public PluginLifecycleClient(NamedPipeBridgeClient bridgeClient) => this.bridgeClient = bridgeClient;

    public async Task<InstalledPluginSnapshot> ListAsync(BridgeInstance instance, CancellationToken cancellationToken)
    {
        var response = await bridgeClient.SendAsync(instance, "list-plugins", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Deserialize<InstalledPluginSnapshot>(JsonOptions)
            ?? throw new InvalidDataException("The bridge returned an empty installed-plugin snapshot.");
    }

    public async Task<PluginBridgeResponse> SetEnabledAsync(
        BridgeInstance instance,
        string internalName,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        var response = await bridgeClient.SendAsync(
            instance,
            enabled ? "enable-plugin" : "disable-plugin",
            new BridgeCommandRequest { Target = internalName },
            cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Message);
        return response;
    }

    public async Task<PluginBridgeResponse> InstallAsync(
        BridgeInstance instance,
        string internalName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        var response = await bridgeClient.SendAsync(
            instance,
            "install-plugin",
            new BridgeCommandRequest { Target = internalName },
            cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Message);
        return response;
    }

    public async Task<PluginBridgeResponse> InstallDevAsync(
        BridgeInstance instance,
        string internalName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        var response = await bridgeClient.SendAsync(
            instance,
            "install-dev-plugin",
            new BridgeCommandRequest { Target = internalName },
            cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Message);
        return response;
    }
}
