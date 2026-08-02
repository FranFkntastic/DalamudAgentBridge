using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace DalamudAgentBridge.Plugin;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public bool EnableScreenshots { get; set; }
    public bool EnableNavigation { get; set; }
    public string AgentBridgeProtectedAccessToken { get; set; } = string.Empty;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
