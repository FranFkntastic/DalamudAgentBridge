using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using System;
using System.Numerics;
using System.Threading;

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
    private readonly DalamudPluginLifecycleService pluginLifecycle;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry = new();
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private int windowOpenState;
    private int requestedCollapsedState;
    private int windowCollapsedState;
    private AgentBridgeViewportRegion? captureRegion;

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
        captureTransactions = new AgentBridgeUiCaptureTransactionManager(
            () => WindowOpen,
            value => WindowOpen = value,
            () => WindowCollapsed,
            RequestWindowCollapsed);
        viewportCapture = new AgentBridgeViewportCaptureService(
            pluginInterface.GetPluginConfigDirectory(),
            configuration.PluginInstanceId,
            () => captureRegion,
            action => framework.RunOnTick(action),
            textureProvider,
            textureReadbackProvider);
        pluginLifecycle = new DalamudPluginLifecycleService(pluginInterface, commandManager, framework);
        bridgeHost = new AgentBridgeHost(
            configuration,
            pluginInterface.GetPluginConfigDirectory(),
            action => framework.RunOnTick(action),
            CreateSnapshot,
            () => reviewRegistry.Snapshot(),
            controlId => reviewRegistry.Review(controlId),
            (controlId, frameId) => reviewRegistry.Invoke(controlId, frameId),
            OpenWindow,
            target => captureTransactions.Begin(target),
            transactionId => captureTransactions.Complete(transactionId),
            transactionId => captureTransactions.Cancel(transactionId),
            viewportCapture.CaptureAsync,
            () => pluginLifecycle.Snapshot(),
            async (internalName, enabled, cancellationToken) =>
                await pluginLifecycle.SetEnabledAsync(internalName, enabled, cancellationToken).ConfigureAwait(false));
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
        captureTransactions.CancelActive();
        bridgeHost.Dispose();
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        commandManager.RemoveHandler(CommandName);
    }

    private bool WindowOpen
    {
        get => Volatile.Read(ref windowOpenState) != 0;
        set => Volatile.Write(ref windowOpenState, value ? 1 : 0);
    }

    private bool WindowCollapsed
    {
        get => Volatile.Read(ref windowCollapsedState) != 0;
        set => Volatile.Write(ref windowCollapsedState, value ? 1 : 0);
    }

    private void OnCommand(string command, string arguments) => RequestWindowOpen();

    private void OpenWindow() => RequestWindowOpen();

    private void RequestWindowOpen()
    {
        WindowOpen = true;
        RequestWindowCollapsed(false);
    }

    private void RequestWindowCollapsed(bool collapsed) =>
        Interlocked.Exchange(ref requestedCollapsedState, collapsed ? 1 : 2);

    private void Draw()
    {
        reviewRegistry.BeginFrame();
        AgentBridgeUiReviewFrame? frame = null;
        try { DrawCore(); }
        finally { frame = reviewRegistry.EndFrame(); }
        if (captureRegion != null && frame != null && captureTransactions.ShouldPresentInMainViewport("bridge.main-window"))
            captureTransactions.MarkRendered("bridge.main-window", frame.FrameId);
    }

    private void DrawCore()
    {
        if (!WindowOpen)
        {
            captureRegion = null;
            return;
        }

        var collapsedRequest = Interlocked.Exchange(ref requestedCollapsedState, 0);
        if (collapsedRequest != 0)
            ImGui.SetNextWindowCollapsed(collapsedRequest == 1, ImGuiCond.Always);
        if (captureTransactions.ShouldPresentInMainViewport("bridge.main-window"))
        {
            var mainViewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowViewport(mainViewport.ID);
            ImGui.SetNextWindowPos(mainViewport.WorkPos + new Vector2(16, 16), ImGuiCond.Always);
        }
        ImGui.SetNextWindowSize(new Vector2(620, 280), ImGuiCond.FirstUseEver);
        var windowOpen = WindowOpen;
        if (!ImGui.Begin("Dalamud Agent Bridge##DalamudAgentBridge", ref windowOpen))
        {
            WindowOpen = windowOpen;
            WindowCollapsed = ImGui.IsWindowCollapsed();
            captureRegion = null;
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
        reviewRegistry.Register(
            "bridge.screenshot-handoff",
            "Allow screenshot handoff to the local bridge utility",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            true,
            configuration.EnableScreenshots,
            configuration.EnableScreenshots ? "Enabled" : "Disabled",
            ToggleScreenshotHandoff);
        ImGui.Spacing();
        ImGui.TextDisabled("Capture is only available through the locally authenticated utility. This standalone plugin provides its own reviewed capture surface.");
            captureRegion = new AgentBridgeViewportRegion(
            ImGui.GetWindowPos(),
            ImGui.GetWindowSize(),
            ImGui.GetMainViewport().Pos,
            ImGui.GetMainViewport().Size,
            DateTimeOffset.UtcNow);
        ImGui.End();
        WindowCollapsed = false;
        WindowOpen = windowOpen;
    }

    private void ToggleScreenshotHandoff()
    {
        configuration.EnableScreenshots = !configuration.EnableScreenshots;
        configuration.Save();
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
        bridgeWindowOpen = WindowOpen,
        reviewFrameId = reviewRegistry.Snapshot().FrameId,
        capabilities = new[] { "open-main-window", "capture-screen", "full-viewport-capture", "get-control-surface", "get-control", "invoke-control", "capture-presentation-transaction", "list-plugins", "enable-plugin", "disable-plugin" },
        screenshotsEnabled = configuration.EnableScreenshots,
    };
}
