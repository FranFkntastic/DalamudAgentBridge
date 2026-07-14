using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

internal sealed class DalamudPluginLifecycleService
{
    private static readonly TimeSpan StateChangeTimeout = TimeSpan.FromSeconds(20);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;

    public DalamudPluginLifecycleService(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
    }

    public PluginLifecycleSnapshot Snapshot() => new(
        DateTimeOffset.UtcNow,
        pluginInterface.InstalledPlugins
            .OrderBy(plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(ToState)
            .ToArray());

    public async Task<PluginLifecycleChangeReceipt> SetEnabledAsync(
        string internalName,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var before = FindRequired(internalName);
        if (string.Equals(before.InternalName, pluginInterface.Manifest.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bridge cannot change its own lifecycle while serving a request.");

        if (before.IsLoaded == enabled)
            return new PluginLifecycleChangeReceipt(enabled, false, before, before, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var requestedAt = DateTimeOffset.UtcNow;
        var command = enabled ? "/xlenableplugin" : "/xldisableplugin";
        var displayName = FindRequiredExposed(internalName).Name;
        var accepted = false;
        await framework.RunOnTick(() => accepted = commandManager.ProcessCommand($"{command} \"{EscapeArgument(displayName)}\"")).ConfigureAwait(false);
        if (!accepted)
            throw new InvalidOperationException($"Dalamud did not accept the {command} lifecycle command.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StateChangeTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = FindRequired(internalName);
            if (current.IsLoaded == enabled)
                return new PluginLifecycleChangeReceipt(enabled, true, before, current, requestedAt, DateTimeOffset.UtcNow);
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private IExposedPlugin FindRequiredExposed(string internalName)
    {
        var matches = pluginInterface.InstalledPlugins.Where(plugin =>
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            0 => throw new KeyNotFoundException($"Plugin '{internalName}' is not installed."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Plugin '{internalName}' is ambiguous: {string.Join(", ", matches.Select(plugin => $"{plugin.Version} ({(plugin.IsDev ? "dev" : "installed")})"))}."),
        };
    }

    private PluginLifecycleState FindRequired(string internalName) => ToState(FindRequiredExposed(internalName));

    private static PluginLifecycleState ToState(IExposedPlugin plugin) => new(
        plugin.InternalName,
        plugin.Name,
        plugin.Version.ToString(),
        plugin.IsLoaded,
        plugin.IsDev,
        plugin.IsTesting,
        plugin.IsThirdParty,
        plugin.IsOutdated,
        plugin.IsBanned,
        plugin.IsOrphaned,
        plugin.IsDecommissioned);

    private static string EscapeArgument(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal);
}

internal sealed record PluginLifecycleSnapshot(DateTimeOffset CapturedAtUtc, IReadOnlyList<PluginLifecycleState> Plugins);

internal sealed record PluginLifecycleState(
    string InternalName,
    string Name,
    string Version,
    bool IsLoaded,
    bool IsDev,
    bool IsTesting,
    bool IsThirdParty,
    bool IsOutdated,
    bool IsBanned,
    bool IsOrphaned,
    bool IsDecommissioned);

internal sealed record PluginLifecycleChangeReceipt(
    bool RequestedEnabled,
    bool Changed,
    PluginLifecycleState Before,
    PluginLifecycleState After,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset CompletedAtUtc);
