using Dalamud.Plugin;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

/// <summary>
/// Installs a development plugin that already exists on disk under this
/// profile's own devPlugins directory but has never been registered with
/// Dalamud. The mechanism is Dalamud's supported one: register the DLL as a
/// watched dev-plugin location, scan, then load. The allowlist is structural:
/// the caller supplies an internal name only, and the assembly must live
/// under the profile devPlugins root with a manifest that proves the name.
/// </summary>
internal sealed class DalamudPluginDevInstallService
{
    private static readonly TimeSpan InstallLoadTimeout = TimeSpan.FromSeconds(60);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly SemaphoreSlim installGate = new(1, 1);

    public DalamudPluginDevInstallService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public async Task<DevPluginInstallReceipt> InstallDevAsync(string internalName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        if (string.Equals(internalName, pluginInterface.Manifest.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bridge cannot install over itself while serving a request.");
        if (pluginInterface.InstalledPlugins.Any(plugin => string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Plugin '{internalName}' is already installed.");

        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallDevCoreAsync(internalName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installGate.Release();
        }
    }

    private async Task<DevPluginInstallReceipt> InstallDevCoreAsync(string internalName, CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var devPluginsRoot = ResolveDevPluginsRoot();
        var candidate = FindCandidate(devPluginsRoot, internalName)
            ?? throw new KeyNotFoundException(
                $"Plugin '{internalName}' was not found under the profile devPlugins root '{devPluginsRoot}'. " +
                "Only assemblies present on disk beneath that root can be dev-installed.");

        var configuration = ResolveDalamudConfiguration();
        EnsureWatchedLocation(configuration, candidate);

        var pluginManager = ResolvePluginManager();
        await InvokeTask(pluginManager, "ScanDevPluginsAsync").ConfigureAwait(false);

        var localPlugin = FindInstalledLocalPlugin(pluginManager, internalName)
            ?? throw new InvalidOperationException(
                $"Plugin '{internalName}' did not appear after the dev plugin scan; check dalamud.log for manifest errors.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallLoadTimeout);

        var loadReason = Enum.Parse(candidate.LoadReasonType, "Installer");
        var loadTask = (Task)localPlugin.GetType()
            .GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(localPlugin, [loadReason, false, CancellationToken.None])!;
        await loadTask.WaitAsync(timeout.Token).ConfigureAwait(false);

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var installed = pluginInterface.InstalledPlugins.FirstOrDefault(plugin =>
                string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
            if (installed is { IsLoaded: true })
            {
                return new DevPluginInstallReceipt(
                    installed.InternalName,
                    installed.Name,
                    installed.Version.ToString(),
                    candidate.DllPath,
                    requestedAt,
                    DateTimeOffset.UtcNow);
            }

            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private string ResolveDevPluginsRoot()
    {
        // Config directory is <profileRoot>/pluginConfigs/<InternalName>.
        var profileRoot = pluginInterface.ConfigDirectory.Parent?.Parent
            ?? throw new InvalidOperationException("Could not resolve the XIVLauncher profile root from the plugin config directory.");
        var devPluginsRoot = Path.Combine(profileRoot.FullName, "devPlugins");
        if (!Directory.Exists(devPluginsRoot))
            throw new InvalidOperationException($"Profile devPlugins root '{devPluginsRoot}' does not exist.");
        return devPluginsRoot;
    }

    private static DevPluginCandidate? FindCandidate(string devPluginsRoot, string internalName)
    {
        foreach (var directory in Directory.EnumerateDirectories(devPluginsRoot))
        {
            foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll"))
            {
                var manifestPath = Path.ChangeExtension(dllPath, ".json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (!document.RootElement.TryGetProperty("InternalName", out var manifestName))
                        continue;
                    if (!string.Equals(manifestName.GetString(), internalName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
                    var loadReasonType = dalamudAssembly.GetType("Dalamud.Plugin.PluginLoadReason")
                        ?? throw new InvalidOperationException("Dalamud PluginLoadReason type was not found.");
                    return new DevPluginCandidate(dllPath, loadReasonType);
                }
                catch (JsonException)
                {
                    // Not a manifest we own; keep scanning.
                }
            }
        }

        return null;
    }

    private static void EnsureWatchedLocation(object configuration, DevPluginCandidate candidate)
    {
        var configurationType = configuration.GetType();
        var locations = (IList)configurationType
            .GetProperty("DevPluginLoadLocations", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(configuration)!;

        foreach (var entry in locations)
        {
            var entryType = entry.GetType();
            var path = entryType.GetProperty("Path")?.GetValue(entry)?.ToString();
            if (!string.Equals(path, candidate.DllPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var enabledProperty = entryType.GetProperty("IsEnabled");
            if (enabledProperty is not null && enabledProperty.GetValue(entry) is false)
                enabledProperty.SetValue(entry, true);

            configurationType.GetMethod("QueueSave", BindingFlags.Instance | BindingFlags.Public)!.Invoke(configuration, null);
            return;
        }

        var settingsType = locations.GetType().IsGenericType
            ? locations.GetType().GetGenericArguments()[0]
            : typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Configuration.DevPluginLocationSettings")
                ?? throw new InvalidOperationException("Dalamud DevPluginLocationSettings type was not found.");
        var settings = Activator.CreateInstance(settingsType)!;
        settingsType.GetProperty("Path")!.SetValue(settings, candidate.DllPath);
        settingsType.GetProperty("IsEnabled")!.SetValue(settings, true);
        locations.Add(settings);

        configurationType.GetMethod("QueueSave", BindingFlags.Instance | BindingFlags.Public)!.Invoke(configuration, null);
    }

    private static object? FindInstalledLocalPlugin(object pluginManager, string internalName)
    {
        var installed = (IEnumerable)pluginManager.GetType()
            .GetProperty("InstalledPlugins", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(pluginManager)!;
        foreach (var plugin in installed)
        {
            var manifest = plugin.GetType().GetProperty("Manifest", BindingFlags.Instance | BindingFlags.Public)?.GetValue(plugin);
            var name = manifest?.GetType().GetProperty("InternalName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(manifest)?.ToString();
            if (string.Equals(name, internalName, StringComparison.OrdinalIgnoreCase))
                return plugin;
        }

        return null;
    }

    private object ResolvePluginManager()
    {
        var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
        var pluginManagerType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.PluginManager")
            ?? throw new InvalidOperationException("Dalamud PluginManager type was not found.");
        var serviceType = dalamudAssembly.GetType("Dalamud.Service`1")?.MakeGenericType(pluginManagerType)
            ?? throw new InvalidOperationException("Dalamud service locator type was not found.");
        return serviceType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null)
            ?? throw new InvalidOperationException("Dalamud PluginManager service is not available.");
    }

    private object ResolveDalamudConfiguration()
    {
        var dalamudAssembly = typeof(IDalamudPluginInterface).Assembly;
        var configurationType = dalamudAssembly.GetType("Dalamud.Configuration.Internal.DalamudConfiguration")
            ?? throw new InvalidOperationException("DalamudConfiguration type was not found.");
        var serviceType = dalamudAssembly.GetType("Dalamud.Service`1")?.MakeGenericType(configurationType)
            ?? throw new InvalidOperationException("Dalamud service locator type was not found.");
        return serviceType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null)
            ?? throw new InvalidOperationException("DalamudConfiguration service is not available.");
    }

    private async Task InvokeTask(object target, string methodName)
    {
        var task = (Task)target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!.Invoke(target, null)!;
        await task.ConfigureAwait(false);
    }

    private sealed record DevPluginCandidate(string DllPath, Type LoadReasonType);
}

internal sealed record DevPluginInstallReceipt(
    string InternalName,
    string Name,
    string InstalledVersion,
    string SourcePath,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset LoadedAtUtc);
