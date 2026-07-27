using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using System;

namespace DalamudAgentBridge.Plugin;

internal sealed class DalamudPluginSurfacePresentationService : IDisposable
{
    private readonly IFramework framework;
    private readonly ReflectedPluginWindowPresentationManager manager;

    public DalamudPluginSurfacePresentationService(
        DalamudPluginSurfaceDiscoveryService discovery,
        IFramework framework,
        TimeSpan? lifetime = null)
    {
        this.framework = framework;
        manager = new ReflectedPluginWindowPresentationManager(
            surfaceId => discovery.TryResolvePresentableWindow(surfaceId, out var resolved) && resolved is not null
                ? new ReflectedPluginWindowPresentationTarget(resolved.Descriptor, resolved.Window)
                : null,
            lifetime);
        framework.Update += OnFrameworkUpdate;
    }

    public AgentBridgePluginSurfacePresentationReceipt Begin(string surfaceId) => manager.Begin(surfaceId);

    public AgentBridgePluginSurfacePresentationResult Restore(string transactionId) => manager.Restore(transactionId);

    public string GetCaptureWindowName(string transactionId)
    {
        var target = manager.GetActiveTarget(transactionId);
        if (target is null)
            throw new InvalidOperationException("Plugin surface capture bounds are unavailable because the presentation lease is stale or its plugin runtime changed.");
        if (!target.Window.IsOpen)
            throw new InvalidOperationException("Plugin surface capture bounds are unavailable because the target window is no longer open.");
        return target.Window.WindowName;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        manager.CancelActive();
    }

    private void OnFrameworkUpdate(IFramework _) => manager.Expire(DateTimeOffset.UtcNow);
}
