using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DalamudAgentBridge.Plugin;

/// <summary>
/// Builds a read-only UI inventory from Dalamud's public plugin API and a bounded inspection of
/// shared IWindowSystem instances. No plugin method or reflected property is invoked.
/// </summary>
internal sealed class DalamudPluginSurfaceDiscoveryService
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ReflectedPluginWindowSurfaceInspector inspector = new();
    private readonly ConditionalWeakTable<object, RuntimeMarker> runtimeMarkers = new();
    private readonly object revisionGate = new();
    private string lastTopology = string.Empty;
    private long catalogRevision = 1;

    public DalamudPluginSurfaceDiscoveryService(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public AgentBridgePluginSurfaceCatalog Snapshot(string? targetInternalName = null)
    {
        var plugins = pluginInterface.InstalledPlugins
            .Where(plugin => string.IsNullOrWhiteSpace(targetInternalName) ||
                string.Equals(plugin.InternalName, targetInternalName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase)
            .Select(Inspect)
            .ToArray();

        var topology = string.Join(
            "\n",
            plugins.SelectMany(plugin =>
                new[] { $"{plugin.InternalName}|{plugin.Version}|{plugin.IsLoaded}|{plugin.HasMainUi}|{plugin.HasConfigUi}|{plugin.RuntimeInstanceId}" }
                    .Concat(plugin.Surfaces.Select(surface => $"{surface.Id}|{surface.Kind}|{surface.Provenance}|{surface.Available}"))));
        lock (revisionGate)
        {
            if (!string.Equals(lastTopology, topology, StringComparison.Ordinal))
            {
                if (lastTopology.Length > 0)
                    catalogRevision++;
                lastTopology = topology;
            }
            return new AgentBridgePluginSurfaceCatalog(DateTimeOffset.UtcNow, catalogRevision, plugins);
        }
    }

    public bool TryResolvePresentableWindow(string surfaceId, out ResolvedPluginWindowSurface? resolved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surfaceId);
        foreach (var plugin in pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded))
        {
            var instance = TryGetPluginInstance(plugin);
            if (instance is null)
                continue;
            var runtimeInstanceId = runtimeMarkers.GetValue(instance, _ => new RuntimeMarker(Guid.NewGuid().ToString("N"))).Id;
            var descriptor = inspector.Inspect(instance, plugin.InternalName, plugin.Name, runtimeInstanceId)
                .SingleOrDefault(candidate => string.Equals(candidate.Id, surfaceId, StringComparison.Ordinal));
            if (descriptor is null ||
                descriptor.Provenance != AgentBridgeSurfaceProvenance.ReflectedWindowSystem ||
                descriptor.Authority != AgentBridgeSurfaceAuthority.ReversiblePresentation ||
                !inspector.TryResolveWindow(instance, plugin.InternalName, surfaceId, out var window) ||
                window is null)
                continue;
            resolved = new ResolvedPluginWindowSurface(descriptor, window);
            return true;
        }
        resolved = null;
        return false;
    }

    private AgentBridgePluginDescriptor Inspect(IExposedPlugin plugin)
    {
        object? instance = null;
        string? runtimeInstanceId = null;
        if (plugin.IsLoaded)
        {
            instance = TryGetPluginInstance(plugin);
            if (instance is not null)
                runtimeInstanceId = runtimeMarkers.GetValue(instance, _ => new RuntimeMarker(Guid.NewGuid().ToString("N"))).Id;
        }

        var surfaces = new List<AgentBridgePluginSurfaceDescriptor>();
        if (plugin.HasMainUi)
            surfaces.Add(PublicSurface(plugin, AgentBridgePluginSurfaceKind.MainUi, "Main UI", "main", runtimeInstanceId));
        if (plugin.HasConfigUi)
            surfaces.Add(PublicSurface(plugin, AgentBridgePluginSurfaceKind.ConfigurationUi, "Configuration UI", "config", runtimeInstanceId));
        if (instance is not null && runtimeInstanceId is not null)
        {
            surfaces.AddRange(inspector.Inspect(
                instance,
                plugin.InternalName,
                plugin.Name,
                runtimeInstanceId));
        }

        return new AgentBridgePluginDescriptor(
            plugin.InternalName,
            plugin.Name,
            plugin.Version.ToString(),
            plugin.IsLoaded,
            plugin.IsDev,
            plugin.HasMainUi,
            plugin.HasConfigUi,
            runtimeInstanceId,
            surfaces.OrderBy(surface => surface.Kind).ThenBy(surface => surface.Label, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static AgentBridgePluginSurfaceDescriptor PublicSurface(
        IExposedPlugin plugin,
        AgentBridgePluginSurfaceKind kind,
        string label,
        string suffix,
        string? runtimeInstanceId) =>
        new(
            $"plugin.{plugin.InternalName}.{suffix}",
            plugin.InternalName,
            plugin.Name,
            label,
            kind,
            AgentBridgeSurfaceProvenance.DalamudPublicApi,
            AgentBridgeSurfaceAuthority.ReadOnly,
            plugin.IsLoaded,
            runtimeInstanceId);

    private static object? TryGetPluginInstance(IExposedPlugin exposed)
    {
        try
        {
            var localPlugin = EnumerateFields(exposed.GetType())
                .Where(field => field.FieldType.FullName == "Dalamud.Plugin.Internal.Types.LocalPlugin")
                .Select(field => field.GetValue(exposed))
                .FirstOrDefault(value => value is not null);
            if (localPlugin is null)
                return null;
            return EnumerateFields(localPlugin.GetType())
                .FirstOrDefault(field => string.Equals(field.Name, "instance", StringComparison.Ordinal))?
                .GetValue(localPlugin);
        }
        catch (Exception exception) when (exception is FieldAccessException or TargetException)
        {
            return null;
        }
    }

    private static IEnumerable<FieldInfo> EnumerateFields(Type type)
    {
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
    }

    private sealed record RuntimeMarker(string Id);
}

internal sealed record ResolvedPluginWindowSurface(
    AgentBridgePluginSurfaceDescriptor Descriptor,
    IWindow Window);
