using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Numerics;

namespace DalamudAgentBridge.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dab";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly Configuration configuration;
    private readonly AgentBridgeViewportCaptureService viewportCapture;
    private readonly AgentBridgeHost bridgeHost;
    private bool windowOpen;
    private AgentBridgeCaptureRegion? captureRegion;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPlayerState playerState,
        IFramework framework,
        ITextureProvider textureProvider,
        ITextureReadbackProvider textureReadbackProvider)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.playerState = playerState;
        this.framework = framework;
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(pluginInterface);
        viewportCapture = new AgentBridgeViewportCaptureService(
            pluginInterface.GetPluginConfigDirectory(),
            configuration.PluginInstanceId,
            () => captureRegion,
            action => framework.RunOnTick(action),
            textureProvider,
            textureReadbackProvider);
        bridgeHost = new AgentBridgeHost(
            configuration,
            pluginInterface.GetPluginConfigDirectory(),
            action => framework.RunOnTick(action),
            CreateSnapshot,
            OpenWindow,
            viewportCapture.CaptureAsync);
        bridgeHost.Start();
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Dalamud Agent Bridge connector status window.",
        });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi += OpenWindow;
    }

    public void Dispose()
    {
        bridgeHost.Dispose();
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        commandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string arguments) => windowOpen = true;

    private void OpenWindow() => windowOpen = true;

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(620, 280), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Dalamud Agent Bridge##DalamudAgentBridge", ref windowOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Agent Bridge Connector");
        ImGui.Separator();
        ImGui.TextWrapped("This private experimental plugin is the in-game connector for the loopback-only bridge utility. It does not expose a network listener or credentials.");
        ImGui.Spacing();
        DrawRow("Process", Environment.ProcessId.ToString());
        DrawRow("Character", playerState.CharacterName ?? "Unavailable");
        DrawRow("World", playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : "Unavailable");
        DrawRow("Bridge", "Authenticated local named pipe (current user)");
        DrawRow("Screenshots", configuration.EnableScreenshots ? "Enabled — encrypted one-time handoff" : "Disabled");
        var screenshotsEnabled = configuration.EnableScreenshots;
        if (ImGui.Checkbox("Allow screenshot handoff to the local bridge utility", ref screenshotsEnabled))
        {
            configuration.EnableScreenshots = screenshotsEnabled;
            configuration.Save();
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Capture is only available through the locally authenticated utility. This plugin provides its own viewport capture; it does not require MarketMafioso.");
        captureRegion = new AgentBridgeCaptureRegion(
            ImGui.GetWindowPos(),
            ImGui.GetWindowSize(),
            ImGui.GetMainViewport().Pos,
            ImGui.GetMainViewport().Size,
            DateTimeOffset.UtcNow);
        ImGui.End();
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TextDisabled($"{label}:");
        ImGui.SameLine(150f);
        ImGui.TextUnformatted(value);
    }

    private object CreateSnapshot() => new
    {
        hostKind = "DalamudAgentBridge",
        pluginVersion = pluginInterface.Manifest.AssemblyVersion,
        processId = Environment.ProcessId,
        characterName = playerState.CharacterName ?? "Unavailable",
        currentWorld = playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : "Unavailable",
        capabilities = new[] { "open-main-window", "capture-screen", "full-viewport-capture" },
        screenshotsEnabled = configuration.EnableScreenshots,
    };
}
