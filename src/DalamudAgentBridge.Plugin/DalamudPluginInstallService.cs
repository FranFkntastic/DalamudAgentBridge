using Dalamud.Plugin;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

internal sealed class DalamudPluginInstallService
{
    private static readonly TimeSpan InstallLoadTimeout = TimeSpan.FromSeconds(60);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly SemaphoreSlim installGate = new(1, 1);

    public DalamudPluginInstallService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public async Task<PluginInstallReceipt> InstallAsync(string internalName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        if (string.Equals(internalName, pluginInterface.Manifest.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bridge cannot install over itself while serving a request.");
        if (pluginInterface.InstalledPlugins.Any(plugin => string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Plugin '{internalName}' is already installed.");

        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(internalName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installGate.Release();
        }
    }

    private async Task<PluginInstallReceipt> InstallCoreAsync(string internalName, CancellationToken cancellationToken)
    {
        if (pluginInterface.InstalledPlugins.Any(plugin => string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Plugin '{internalName}' is already installed.");

        var requestedAt = DateTimeOffset.UtcNow;
        var pluginManager = ResolvePluginManager();
        var manifest = FindAvailableManifest(pluginManager, internalName);
        if (manifest is null)
        {
            await InvokeTask(pluginManager, "ReloadAllReposAsync").ConfigureAwait(false);
            manifest = FindAvailableManifest(pluginManager, internalName);
            if (manifest is null)
                throw new KeyNotFoundException($"Plugin '{internalName}' is not available from any configured plugin repository.");
        }

        var availableVersion = GetString(manifest, "AssemblyVersion") ?? "unknown";
        var sourceRepo = GetNestedString(manifest, "SourceRepo", "PluginMasterUrl") ?? "unknown";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallLoadTimeout);

        var installTask = (Task)pluginManager.GetType()
            .GetMethod("InstallPluginAsync", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(pluginManager, [manifest, false, PluginLoadReason.Installer])!;
        await installTask.WaitAsync(timeout.Token).ConfigureAwait(false);

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var installed = pluginInterface.InstalledPlugins.FirstOrDefault(plugin =>
                string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
            if (installed is { IsLoaded: true })
            {
                return new PluginInstallReceipt(
                    installed.InternalName,
                    installed.Name,
                    installed.Version.ToString(),
                    availableVersion,
                    sourceRepo,
                    requestedAt,
                    DateTimeOffset.UtcNow);
            }
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
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

    private static object? FindAvailableManifest(object pluginManager, string internalName)
    {
        var available = (IEnumerable)pluginManager.GetType()
            .GetProperty("AvailablePlugins", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(pluginManager)!;
        foreach (var manifest in available)
        {
            if (string.Equals(GetString(manifest, "InternalName"), internalName, StringComparison.OrdinalIgnoreCase))
                return manifest;
        }
        return null;
    }

    private async Task InvokeTask(object target, string methodName)
    {
        var task = (Task)target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!.Invoke(target, null)!;
        await task.ConfigureAwait(false);
    }

    private static string? GetString(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(instance)?.ToString();

    private static string? GetNestedString(object instance, string propertyName, string nestedPropertyName)
    {
        var nested = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);
        return nested is null ? null : GetString(nested, nestedPropertyName);
    }
}

internal sealed record PluginInstallReceipt(
    string InternalName,
    string Name,
    string InstalledVersion,
    string RepositoryVersion,
    string SourceRepository,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset LoadedAtUtc);
