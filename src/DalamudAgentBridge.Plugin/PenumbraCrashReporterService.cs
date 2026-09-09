using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace DalamudAgentBridge.Plugin;

/// <summary>Bounded adapter for Penumbra's own persistent crash logging setting.</summary>
internal sealed class PenumbraCrashReporterService(Func<object> resolveInstance)
{
    private readonly ConditionalWeakTable<object, InstanceIdentity> identities = new();

    public PenumbraCrashReporterSnapshot Snapshot()
    {
        try
        {
            var target = Resolve();
            return Read(target);
        }
        catch (Exception ex)
        {
            return new(false, null, null, null, null, Environment.ProcessId, null, null,
                ex.GetBaseException().Message);
        }
    }

    public PenumbraCrashReporterSnapshot SetEnabled(bool enabled, string expectedInstanceId, bool? expectedEnabled)
    {
        var target = Resolve();
        var before = Read(target);
        if (before.InstanceId != expectedInstanceId || before.Enabled != expectedEnabled)
            throw new InvalidOperationException("Penumbra changed since review; obtain a fresh reporter review.");

        // This is the same setter used by Penumbra's settings checkbox. It persists
        // the setting and invokes the service's enable/disable lifecycle event.
        target.Enabled.SetValue(target.Config, (bool?)enabled);
        if ((bool)Property(target.Service, "IsRunning").GetValue(target.Service)! != enabled)
        {
            // Reconcile a saved-enabled but stopped reporter without toggling mods.
            var method = target.Service.GetType().GetMethod(enabled ? "Enable" : "Disable", Type.EmptyTypes)
                ?? throw new InvalidOperationException("Penumbra reporter lifecycle is unsupported.");
            method.Invoke(target.Service, null);
        }
        var after = Read(target);
        if (after.Enabled != enabled || after.Running != enabled)
            throw new InvalidOperationException("Penumbra did not reach the requested reporter state.");
        return after;
    }

    private Target Resolve()
    {
        var instance = resolveInstance();
        if (instance.GetType().FullName != "Penumbra.Penumbra")
            throw new InvalidOperationException("Unsupported Penumbra plugin type.");
        var configRoot = Field(instance, "_config");
        var config = configRoot.GetType().GetField("Advanced")?.GetValue(configRoot)
            ?? throw new InvalidOperationException("Penumbra advanced configuration is unavailable.");
        var enabled = Property(config, "UseCrashHandler");
        if (enabled.PropertyType != typeof(bool?) || !enabled.CanWrite)
            throw new InvalidOperationException("Unsupported Penumbra crash logging setting.");
        var services = Field(instance, "_services");
        var serviceType = instance.GetType().Assembly.GetType("Penumbra.Services.CrashHandlerService")
            ?? throw new InvalidOperationException("Penumbra crash reporter service is unavailable.");
        var getter = services.GetType().GetMethods()
            .SingleOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition
                && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0)
            ?? throw new InvalidOperationException("Unsupported Penumbra service resolver.");
        var service = getter.MakeGenericMethod(serviceType).Invoke(services, null)
            ?? throw new InvalidOperationException("Penumbra crash reporter service is not ready.");
        return new(instance, config, enabled, service, instance.GetType().Assembly.GetName().Version?.ToString() ?? "Unknown");
    }

    private PenumbraCrashReporterSnapshot Read(Target target) => new(
        true,
        (bool?)target.Enabled.GetValue(target.Config),
        (bool)Property(target.Service, "IsRunning").GetValue(target.Service)!,
        identities.GetValue(target.Instance, _ => new()).Id,
        target.Version,
        Environment.ProcessId,
        (int)Property(target.Service, "ChildProcessId").GetValue(target.Service)!,
        (string?)Property(target.Service, "LogPath").GetValue(target.Service),
        null);

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Required Penumbra field {name} is unavailable.");

    private static PropertyInfo Property(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException($"Required Penumbra property {name} is unavailable.");

    private sealed record Target(object Instance, object Config, PropertyInfo Enabled, object Service, string Version);
    private sealed class InstanceIdentity { public string Id { get; } = Guid.NewGuid().ToString("N"); }
}

internal sealed record PenumbraCrashReporterSnapshot(
    bool Available, bool? Enabled, bool? Running, string? InstanceId, string? PenumbraVersion,
    int ProcessId, int? ReporterProcessId, string? LogPath, string? Error);
