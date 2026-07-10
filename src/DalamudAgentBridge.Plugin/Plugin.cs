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
    private bool windowOpen;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPlayerState playerState)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.playerState = playerState;
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
        DrawRow("Local dashboard", "http://127.0.0.1:45831");
        DrawRow("Repository", "http://127.0.0.1:45831/repository/repo.json");
        ImGui.Spacing();
        ImGui.TextDisabled("Current slice: install/distribution proof and connector identity. Visual capture remains a future bridge capability.");
        ImGui.End();
    }

    private static void DrawRow(string label, string value)
    {
        ImGui.TextDisabled($"{label}:");
        ImGui.SameLine(150f);
        ImGui.TextUnformatted(value);
    }
}
