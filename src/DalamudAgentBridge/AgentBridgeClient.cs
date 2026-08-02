using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

/// <summary>Canonical typed client used by HTTP, CLI, MCP and tests.</summary>
public sealed class AgentBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly BridgeRegistry registry;
    private readonly NamedPipeBridgeClient pipe;
    private readonly ReviewedControlPresentationService presentations;
    private readonly DalamudLogWatcher logs;

    public AgentBridgeClient(
        BridgeRegistry registry,
        NamedPipeBridgeClient pipe,
        ReviewedControlPresentationService presentations,
        DalamudLogWatcher logs)
    {
        this.registry = registry;
        this.pipe = pipe;
        this.presentations = presentations;
        this.logs = logs;
    }

    public IReadOnlyList<BridgeInstanceView> List() => registry.Discover().Select(ToView).ToArray();

    public BridgeInstance Resolve(BridgeTargetSelector selector) => registry.Resolve(selector);

    public async Task<AgentBridgeManifest> GetManifestAsync(BridgeTargetSelector selector, CancellationToken cancellationToken) =>
        await GetManifestAsync(Resolve(selector), cancellationToken).ConfigureAwait(false);

    public async Task<AgentBridgeManifest> GetManifestAsync(BridgeInstance instance, CancellationToken cancellationToken)
    {
        var response = await pipe.SendAsync(instance, "get-manifest", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException($"{instance.Id} does not expose a versioned manifest: {response.Message}");
        return receipt.Deserialize<AgentBridgeManifest>(JsonOptions)
            ?? throw new InvalidDataException("The bridge returned an empty manifest.");
    }

    public async Task<BridgeHealthReceipt> GetHealthAsync(BridgeTargetSelector selector, CancellationToken cancellationToken)
    {
        var instance = Resolve(selector);
        try
        {
            var manifest = await GetManifestAsync(instance, cancellationToken).ConfigureAwait(false);
            return new BridgeHealthReceipt(ToView(instance), true, "Bridge is reachable and its manifest is valid.", manifest, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
        {
            return new BridgeHealthReceipt(ToView(instance), false, exception.Message, null, DateTimeOffset.UtcNow);
        }
    }

    public async Task<JsonElement> GetSnapshotAsync(BridgeTargetSelector selector, CancellationToken cancellationToken) =>
        await GetSnapshotAsync(Resolve(selector), cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> GetSnapshotAsync(BridgeInstance instance, CancellationToken cancellationToken)
    {
        var response = await pipe.SendAsync(instance, "get-snapshot", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Clone();
    }

    public Task<JsonElement> GetSituationAsync(BridgeTargetSelector selector, CancellationToken cancellationToken) =>
        GetSituationAsync(Resolve(selector), cancellationToken);

    public async Task<JsonElement> GetSituationAsync(BridgeInstance instance, CancellationToken cancellationToken)
    {
        var response = await pipe.SendAsync(instance, "get-situation", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Clone();
    }

    public async Task<JsonElement> GetNavigationAsync(BridgeTargetSelector selector, CancellationToken cancellationToken)
    {
        var response = await pipe.SendAsync(Resolve(selector), "get-navigation", null, cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Clone();
    }

    public Task<PluginBridgeResponse> NavigateAsync(
        BridgeTargetSelector selector,
        NavigationTargetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = JsonSerializer.SerializeToElement(request, JsonOptions);
        return pipe.SendAsync(
            Resolve(selector),
            "navigate-to",
            new BridgeCommandRequest { Arguments = arguments },
            cancellationToken);
    }

    public Task<PluginBridgeResponse> CancelNavigationAsync(
        BridgeTargetSelector selector,
        string? operationId,
        CancellationToken cancellationToken) =>
        pipe.SendAsync(
            Resolve(selector),
            "cancel-navigation",
            new BridgeCommandRequest { OperationId = operationId },
            cancellationToken);

    public async Task<AgentBridgePluginSurfaceCatalog> GetPluginSurfaceCatalogAsync(
        string? targetPlugin,
        string? profile,
        int? processId,
        CancellationToken cancellationToken)
    {
        var connector = Resolve(new BridgeTargetSelector("DalamudAgentBridge", profile, processId));
        var response = await pipe.SendAsync(
            connector,
            "get-plugin-surfaces",
            string.IsNullOrWhiteSpace(targetPlugin) ? null : new BridgeCommandRequest { Target = targetPlugin },
            cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Deserialize<AgentBridgePluginSurfaceCatalog>(JsonOptions)
            ?? throw new InvalidDataException("The bridge returned an empty plugin surface catalog.");
    }

    public async Task<AgentBridgePluginSurfacePresentationReceipt> BeginPluginSurfacePresentationAsync(
        string plugin,
        string surfaceId,
        string? profile,
        int? processId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        var catalog = await GetPluginSurfaceCatalogAsync(plugin, profile, processId, cancellationToken).ConfigureAwait(false);
        var descriptor = catalog.Plugins
            .Where(candidate => string.Equals(candidate.InternalName, plugin, StringComparison.OrdinalIgnoreCase))
            .SelectMany(candidate => candidate.Surfaces)
            .SingleOrDefault(candidate => string.Equals(candidate.Id, surfaceId, StringComparison.Ordinal));
        if (descriptor is null)
            throw new InvalidOperationException($"Surface {surfaceId} is not present in {plugin}'s current runtime catalog.");
        if (descriptor.Authority != AgentBridgeSurfaceAuthority.ReversiblePresentation ||
            descriptor.Provenance != AgentBridgeSurfaceProvenance.ReflectedWindowSystem)
            throw new InvalidOperationException($"Surface {surfaceId} is read-only and cannot be presented reversibly.");

        var connector = Resolve(new BridgeTargetSelector("DalamudAgentBridge", profile, processId));
        var response = await pipe.SendAsync(
            connector,
            "begin-plugin-surface-presentation",
            new BridgeCommandRequest { Target = surfaceId },
            cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Deserialize<AgentBridgePluginSurfacePresentationReceipt>(JsonOptions)
            ?? throw new InvalidDataException("The bridge returned an invalid plugin surface presentation receipt.");
    }

    public async Task<AgentBridgePluginSurfacePresentationResult> RestorePluginSurfacePresentationAsync(
        string transactionId,
        string? profile,
        int? processId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        var connector = Resolve(new BridgeTargetSelector("DalamudAgentBridge", profile, processId));
        var response = await pipe.SendAsync(
            connector,
            "restore-plugin-surface-presentation",
            new BridgeCommandRequest { TransactionId = transactionId },
            cancellationToken).ConfigureAwait(false);
        var result = response.Receipt is { } receipt
            ? receipt.Deserialize<AgentBridgePluginSurfacePresentationResult>(JsonOptions)
            : null;
        if (result is not null)
            return result;
        return new AgentBridgePluginSurfacePresentationResult(response.Success, response.Message, transactionId);
    }

    public async Task<BridgeWaitReceipt> WaitForSnapshotAsync(
        BridgeTargetSelector selector,
        BridgeWaitCondition condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var startedAt = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var attempts = 0;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var instance = Resolve(selector);
            var snapshot = await GetSnapshotAsync(instance, deadline.Token).ConfigureAwait(false);
            attempts++;
            if (Matches(snapshot, condition))
                return new BridgeWaitReceipt(instance.Id, condition, snapshot, startedAt, DateTimeOffset.UtcNow, attempts);
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    public async Task<BridgeActionWorkflowReceipt> ActAndObserveAsync(
        BridgeTargetSelector selector,
        ReviewedControlActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = DateTimeOffset.UtcNow;
        var instance = Resolve(selector);
        var logCursor = logs.Read(instance, null, 1).NextCursor;
        var action = await presentations.PresentAndInvokeAsync(instance, request, cancellationToken).ConfigureAwait(false);
        var operationId = action.Invocation.OperationId;
        if (string.IsNullOrWhiteSpace(operationId) && action.Invocation.Receipt is { } invocationReceipt &&
            invocationReceipt.TryGetProperty("action", out var actionResult) &&
            actionResult.ValueKind == JsonValueKind.Object &&
            actionResult.TryGetProperty("operationId", out var operationValue))
            operationId = operationValue.GetString();
        AgentBridgeOperationSnapshot? operation = null;
        var completionTimeout = TimeSpan.FromMilliseconds(Math.Clamp(request.CompletionTimeoutMilliseconds ?? 30_000, 250, 300_000));
        if (request.WaitForCompletion && !string.IsNullOrWhiteSpace(operationId))
            operation = await WaitForOperationAsync(instance, operationId, completionTimeout, cancellationToken).ConfigureAwait(false);
        else if (request.WaitForCompletion && request.CompletionCondition is not null)
            await WaitForSnapshotAsync(selector, request.CompletionCondition, completionTimeout, cancellationToken).ConfigureAwait(false);

        var finalInstance = Resolve(selector);
        var snapshot = await GetSnapshotAsync(finalInstance, cancellationToken).ConfigureAwait(false);
        var actionLogs = logs.Read(finalInstance, logCursor, 1000);
        return new BridgeActionWorkflowReceipt(
            ToView(finalInstance), action, operation, snapshot, actionLogs, startedAt, DateTimeOffset.UtcNow);
    }

    public DalamudLogRead ReadLogs(BridgeTargetSelector selector, long? cursor = null, int? limit = null) =>
        logs.Read(Resolve(selector), cursor, limit);

    public async Task<ChatLogRead> ReadChatLogAsync(BridgeTargetSelector selector, long? cursor, int? limit, CancellationToken cancellationToken)
    {
        var instance = Resolve(selector);
        JsonElement? arguments = null;
        if (cursor is not null || limit is not null)
            arguments = JsonSerializer.SerializeToElement(new { cursor, limit }, JsonOptions);
        var response = await pipe.SendAsync(
            instance,
            "get-chat-log",
            arguments is null ? null : new BridgeCommandRequest { Arguments = arguments },
            cancellationToken).ConfigureAwait(false);
        if (!response.Success || response.Receipt is not { } receipt)
            throw new InvalidOperationException(response.Message);
        return receipt.Deserialize<ChatLogRead>(JsonOptions)
            ?? throw new InvalidDataException("The bridge returned an empty chat log read.");
    }

    private async Task<AgentBridgeOperationSnapshot> WaitForOperationAsync(
        BridgeInstance instance,
        string operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (true)
        {
            var response = await pipe.SendAsync(
                instance,
                "get-operation",
                new BridgeCommandRequest { OperationId = operationId },
                deadline.Token).ConfigureAwait(false);
            if (!response.Success || response.Receipt is not { } receipt)
                throw new InvalidOperationException(response.Message);
            var operation = receipt.Deserialize<AgentBridgeOperationSnapshot>(JsonOptions)
                ?? throw new InvalidDataException("The bridge returned an empty operation receipt.");
            if (operation.State is AgentBridgeOperationState.Succeeded)
                return operation;
            if (operation.State is AgentBridgeOperationState.Failed or AgentBridgeOperationState.Cancelled)
                throw new InvalidOperationException($"Bridge operation {operation.Id} {operation.State}: {operation.Message}");
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        }
    }

    internal static BridgeInstanceView ToView(BridgeInstance instance) => new(
        instance.Id,
        instance.PluginName,
        instance.PipeName,
        instance.ProcessId,
        instance.SchemaVersion,
        instance.PluginInstanceId,
        instance.PluginInternalName,
        instance.RuntimeInstanceId,
        instance.ProfileId,
        instance.ProfileAlias,
        instance.ProtocolVersion);

    internal static bool Matches(JsonElement snapshot, BridgeWaitCondition condition)
    {
        var found = TryResolvePath(snapshot, condition.Path, out var value);
        if (condition.Exists is { } expectedExistence && found != expectedExistence)
            return false;
        if (condition.ExpectedValue is null)
            return condition.Exists is not null || found;
        return found && string.Equals(GetComparableValue(value), condition.ExpectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolvePath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private static string GetComparableValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };
}
