using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        bool? isDev,
        CancellationToken cancellationToken)
    {
        var managedPlugin = FindRequiredExposed(internalName, enabled, isDev);
        var before = ToState(managedPlugin);
        if (string.Equals(before.InternalName, pluginInterface.Manifest.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bridge cannot change its own lifecycle while serving a request.");

        if (before.IsLoaded == enabled)
            return new PluginLifecycleChangeReceipt(enabled, false, before, before, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        if (isDev is not null)
            return await SetExactEnabledAsync(managedPlugin, before, enabled, cancellationToken).ConfigureAwait(false);

        var requestedAt = DateTimeOffset.UtcNow;
        var command = enabled ? "/xlenableplugin" : "/xldisableplugin";
        var displayName = managedPlugin.Name;
        var accepted = false;
        await framework.RunOnTick(() => accepted = commandManager.ProcessCommand($"{command} \"{EscapeArgument(displayName)}\"")).ConfigureAwait(false);
        if (!accepted)
            throw new InvalidOperationException($"Dalamud did not accept the {command} lifecycle command.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StateChangeTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = FindRequired(before.InternalName, before.Version, before.IsDev);
            if (current.IsLoaded == enabled)
                return new PluginLifecycleChangeReceipt(enabled, true, before, current, requestedAt, DateTimeOffset.UtcNow);
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<PluginLifecycleChangeReceipt> SetExactEnabledAsync(
        IExposedPlugin managedPlugin,
        PluginLifecycleState before,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StateChangeTimeout);

        var plugin = ResolveLocalPlugin(managedPlugin);
        if (enabled)
        {
            await SetProfileStateAsync(plugin, before.InternalName, true).WaitAsync(timeout.Token).ConfigureAwait(false);
            await LoadExactAsync(plugin, timeout.Token).ConfigureAwait(false);
        }
        else
        {
            await InvokeTask(plugin, "UnloadAsync").WaitAsync(timeout.Token).ConfigureAwait(false);
            await SetProfileStateAsync(plugin, before.InternalName, false).WaitAsync(timeout.Token).ConfigureAwait(false);
        }

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var current = FindRequired(before.InternalName, before.Version, before.IsDev);
            if (current.IsLoaded == enabled)
                return new PluginLifecycleChangeReceipt(enabled, true, before, current, requestedAt, DateTimeOffset.UtcNow);
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task LoadExactAsync(object plugin, CancellationToken cancellationToken)
    {
        var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
        var loadReasonType = dalamudAssembly.GetType("Dalamud.Plugin.PluginLoadReason")
            ?? throw new InvalidOperationException("Dalamud PluginLoadReason type was not found.");
        var loadReason = Enum.Parse(loadReasonType, "Installer");
        var load = plugin.GetType().GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Dalamud plugin LoadAsync method was not found.");
        var task = (Task?)load.Invoke(plugin, [loadReason, false, cancellationToken])
            ?? throw new InvalidOperationException("Dalamud plugin LoadAsync returned no task.");
        await task.ConfigureAwait(false);
    }

    private static async Task SetProfileStateAsync(object plugin, string internalName, bool enabled)
    {
        var pluginIdProperty = plugin.GetType().GetProperty("EffectiveWorkingPluginId", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("Dalamud plugin identity was not found.");
        if (pluginIdProperty.GetValue(plugin) is not Guid pluginId)
            throw new InvalidOperationException("Dalamud plugin identity was invalid.");

        var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
        var profileManagerType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.Profiles.ProfileManager")
            ?? throw new InvalidOperationException("Dalamud ProfileManager type was not found.");
        var serviceType = dalamudAssembly.GetType("Dalamud.Service`1")?.MakeGenericType(profileManagerType)
            ?? throw new InvalidOperationException("Dalamud service locator type was not found.");
        var profileManager = serviceType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null)
            ?? throw new InvalidOperationException("Dalamud ProfileManager service is not available.");
        var profiles = (IEnumerable?)profileManagerType.GetProperty("Profiles", BindingFlags.Instance | BindingFlags.Public)?.GetValue(profileManager)
            ?? throw new InvalidOperationException("Dalamud profiles were not available.");

        var matches = profiles.Cast<object>().Where(profile =>
        {
            var wantsPlugin = profile.GetType().GetMethod("WantsPlugin", BindingFlags.Instance | BindingFlags.Public);
            return wantsPlugin?.Invoke(profile, [pluginId]) is not null;
        }).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Plugin '{internalName}' belongs to {matches.Length} profiles; exact lifecycle changes require one profile.");

        var update = matches[0].GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "AddOrUpdateAsync" && method.GetParameters().Length == 4)
            ?? throw new InvalidOperationException("Dalamud profile update method was not found.");
        var task = (Task?)update.Invoke(matches[0], [pluginId, internalName, enabled, false])
            ?? throw new InvalidOperationException("Dalamud profile update returned no task.");
        await task.ConfigureAwait(false);
    }

    private static Task InvokeTask(object target, string methodName)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().All(parameter => parameter.IsOptional))
            ?? throw new InvalidOperationException($"Dalamud plugin {methodName} method was not found.");
        var arguments = method.GetParameters()
            .Select(parameter => parameter.DefaultValue is DBNull ? Type.Missing : parameter.DefaultValue)
            .ToArray();
        return (Task?)method.Invoke(target, arguments)
            ?? throw new InvalidOperationException($"Dalamud plugin {methodName} returned no task.");
    }

    private static object ResolveLocalPlugin(IExposedPlugin exposedPlugin)
    {
        var candidates = exposedPlugin.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => field.GetValue(exposedPlugin))
            .Where(value => value is not null && IsLocalPluginType(value.GetType()))
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0]!,
            0 => throw new InvalidOperationException("Dalamud exposed plugin did not contain its managed plugin instance."),
            _ => throw new InvalidOperationException("Dalamud exposed plugin contained multiple managed plugin instances."),
        };
    }

    private static bool IsLocalPluginType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, "Dalamud.Plugin.Internal.Types.LocalPlugin", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private IExposedPlugin FindRequiredExposed(string internalName, bool enabling, bool? isDev)
    {
        var matches = pluginInterface.InstalledPlugins.Where(plugin =>
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (isDev is not null)
            matches = matches.Where(plugin => plugin.IsDev == isDev.Value).ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (matches.Length == 0)
            throw new KeyNotFoundException($"Plugin '{internalName}' is not installed.");

        var preferred = enabling
            ? matches.Where(plugin => plugin.IsDev).ToArray()
            : matches.Where(plugin => plugin.IsLoaded).ToArray();
        return preferred.Length switch
        {
            1 => preferred[0],
            _ => throw new InvalidOperationException(
                $"Plugin '{internalName}' is ambiguous: {string.Join(", ", matches.Select(plugin => $"{plugin.Version} ({(plugin.IsDev ? "dev" : "installed")})"))}."),
        };
    }

    private PluginLifecycleState FindRequired(string internalName, string version, bool isDev)
    {
        var match = pluginInterface.InstalledPlugins.SingleOrDefault(plugin =>
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(plugin.Version.ToString(), version, StringComparison.OrdinalIgnoreCase) &&
            plugin.IsDev == isDev);
        return match is null
            ? throw new KeyNotFoundException($"Managed plugin '{internalName}' {version} is no longer installed.")
            : ToState(match);
    }

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
        plugin.IsDecommissioned,
        plugin.HasMainUi,
        plugin.HasConfigUi);

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
    bool IsDecommissioned,
    bool HasMainUi,
    bool HasConfigUi);

internal sealed record PluginLifecycleChangeReceipt(
    bool RequestedEnabled,
    bool Changed,
    PluginLifecycleState Before,
    PluginLifecycleState After,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset CompletedAtUtc);
