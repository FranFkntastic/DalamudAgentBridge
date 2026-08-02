using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Observations;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chatGui;
    private readonly ChatLogBuffer chatLogBuffer = new();
    private readonly Configuration configuration;
    private readonly AgentBridgeViewportCaptureService viewportCapture;
    private readonly AgentBridgeHost bridgeHost;
    private readonly DalamudPluginLifecycleService pluginLifecycle;
    private readonly DalamudPluginInstallService pluginInstall;
    private readonly DalamudPluginDevInstallService pluginDevInstall;
    private readonly DalamudPluginSurfaceDiscoveryService pluginSurfaceDiscovery;
    private readonly DalamudPluginSurfacePresentationService pluginSurfacePresentation;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry = new();
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly DalamudRenderedUiTextActionDispatcher renderedTextActions;
    private readonly DalamudLifestreamLogin lifestreamLogin;
    private readonly NativeSlashCommandPolicy nativeSlashCommandPolicy;
    private readonly DalamudSharedObservationHost? sharedObservationHost;
    private int windowOpenState;
    private int requestedCollapsedState;
    private int windowCollapsedState;
    private AgentBridgeViewportRegion? captureRegion;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPlayerState playerState,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IChatGui chatGui,
        IGameInventory gameInventory,
        IAddonLifecycle addonLifecycle,
        IPluginLog pluginLog,
        IDataManager dataManager,
        IGameGui gameGui,
        ITextureProvider textureProvider,
        ITextureReadbackProvider textureReadbackProvider)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.playerState = playerState;
        this.framework = framework;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.chatGui = chatGui;
        nativeSlashCommandPolicy = CreateNativeSlashCommandPolicy(dataManager);
        this.chatGui.ChatMessage += OnChatMessage;
        renderedTextActions = new(gameGui);
        lifestreamLogin = new(pluginInterface);
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(pluginInterface);
        captureTransactions = new AgentBridgeUiCaptureTransactionManager(
            () => WindowOpen,
            value => WindowOpen = value,
            () => WindowCollapsed,
            RequestWindowCollapsed,
            RestoreCaptureCollapseState);
        viewportCapture = new AgentBridgeViewportCaptureService(
            pluginInterface.GetPluginConfigDirectory(),
            configuration.PluginInstanceId,
            () => captureRegion,
            action => framework.RunOnTick(action),
            textureProvider,
            textureReadbackProvider);
        pluginLifecycle = new DalamudPluginLifecycleService(pluginInterface, commandManager, framework);
        pluginInstall = new DalamudPluginInstallService(pluginInterface);
        pluginDevInstall = new DalamudPluginDevInstallService(pluginInterface);
        pluginSurfaceDiscovery = new DalamudPluginSurfaceDiscoveryService(pluginInterface);
        pluginSurfacePresentation = new DalamudPluginSurfacePresentationService(pluginSurfaceDiscovery, framework);
        bridgeHost = new AgentBridgeHost(
            configuration,
            pluginInterface.GetPluginConfigDirectory(),
            pluginInterface.AssemblyLocation.FullName,
            action => framework.RunOnTick(action),
            CreateSnapshot,
            () => reviewRegistry.Snapshot(),
            controlId => reviewRegistry.Review(controlId),
            (controlId, frameId) => reviewRegistry.Invoke(controlId, frameId),
            OpenWindow,
            GetCaptureSurfaces,
            target => captureTransactions.Begin(target),
            transactionId => captureTransactions.Complete(transactionId),
            transactionId => captureTransactions.Cancel(transactionId),
            viewportCapture.CaptureAsync,
            (transactionId, cancellationToken) => viewportCapture.CaptureWindowAsync(
                () => pluginSurfacePresentation.GetCaptureWindowName(transactionId),
                "PluginSurface",
                cancellationToken),
            () => pluginLifecycle.Snapshot(),
            target => pluginSurfaceDiscovery.Snapshot(target),
            pluginSurfacePresentation.Begin,
            pluginSurfacePresentation.Restore,
            async (internalName, enabled, cancellationToken) =>
                await pluginLifecycle.SetEnabledAsync(internalName, enabled, cancellationToken).ConfigureAwait(false),
            async (internalName, cancellationToken) =>
                await pluginInstall.InstallAsync(internalName, cancellationToken).ConfigureAwait(false),
            async (internalName, cancellationToken) =>
                await pluginDevInstall.InstallDevAsync(internalName, cancellationToken).ConfigureAwait(false),
            CreateLoginSnapshot,
            BeginLogin,
            reviewRegistry.ActionCatalog,
            () => reviewRegistry.CatalogRevision,
            SendChatLine,
            chatLogBuffer.Read);
        bridgeHost.Start();
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Dalamud Agent Bridge connector status window.",
        });
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi += OpenWindow;
        try
        {
            sharedObservationHost = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = pluginInterface.GetPluginConfigDirectory(),
                PluginName = "DalamudAgentBridge",
                PluginInstanceId = Guid.NewGuid().ToString("N"),
                GameBuild = Franthropy.Dalamud.Diagnostics.GamePatchCompatibilityGate.ReadCurrentGameVersion(),
                GameInventory = gameInventory,
                PlayerState = playerState,
                AddonLifecycle = addonLifecycle,
                Diagnostic = (message, exception) =>
                {
                    if (exception is null) pluginLog.Warning(message);
                    else pluginLog.Error(exception, message);
                },
            });
            sharedObservationHost.Start();
        }
        catch (Exception exception)
        {
            sharedObservationHost?.Dispose();
            sharedObservationHost = null;
            pluginLog.Error(exception, "Dalamud Agent Bridge shared-observation hosting is unavailable.");
        }
    }

    public void Dispose()
    {
        captureTransactions.CancelActive();
        bridgeHost.Dispose();
        chatGui.ChatMessage -= OnChatMessage;
        viewportCapture.Dispose();
        pluginSurfacePresentation.Dispose();
        sharedObservationHost?.Dispose();
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

    private void OnChatMessage(IHandleableChatMessage message) =>
        chatLogBuffer.Record(
            (int)message.LogKind,
            message.LogKind.ToString(),
            message.Timestamp,
            message.Sender.TextValue,
            message.Message.TextValue);

    private unsafe SlashCommandSubmission SendChatLine(string line)
    {
        var policyDecision = nativeSlashCommandPolicy.Evaluate(line);
        if (!policyDecision.Allowed)
            return SlashCommandSubmission.Rejected(policyDecision.Message);

        if (commandManager.ProcessCommand(policyDecision.CommandLine))
            return SlashCommandSubmission.PluginCommand();

        var uiModule = UIModule.Instance();
        var shellModule = RaptureShellModule.Instance();
        if (uiModule == null || shellModule == null)
            return SlashCommandSubmission.Rejected("The native game command shell is unavailable.");

        try
        {
            // The user's CWLS2 is intentionally reserved as a sink. Resetting
            // ambient chat immediately before fallback contains any later
            // plain-text typo, while explicit channel commands remain blocked.
            using var sinkCommand = new Utf8String("/cwlinkshell2");
            shellModule->ExecuteCommandInner(&sinkCommand, uiModule);

            using var command = new Utf8String(policyDecision.CommandLine);
            shellModule->ExecuteCommandInner(&command, uiModule);
            return SlashCommandSubmission.NativeCommand();
        }
        catch (Exception exception)
        {
            return SlashCommandSubmission.Rejected($"Native game command submission failed: {exception.Message}");
        }
    }

    private static NativeSlashCommandPolicy CreateNativeSlashCommandPolicy(IDataManager dataManager)
    {
        ClientLanguage[] languages =
        [
            ClientLanguage.Japanese,
            ClientLanguage.English,
            ClientLanguage.German,
            ClientLanguage.French,
        ];
        var commands = languages
            .SelectMany(language => dataManager
                .GetExcelSheet<TextCommand>(language)
                .Select(command => new NativeTextCommandDefinition(
                    command.RowId,
                    command.Param.RowId,
                    [
                        command.Alias.ToString(),
                        command.ShortAlias.ToString(),
                        command.Command.ToString(),
                        command.ShortCommand.ToString(),
                    ])))
            .GroupBy(command => command.RowId)
            .Select(MergeLocalizedCommandRows)
            .ToArray();
        var emoteCommandRowIds = languages
            .SelectMany(language => dataManager
                .GetExcelSheet<Emote>(language)
                .Select(emote => emote.TextCommand.RowId))
            .Where(rowId => rowId != 0)
            .Distinct()
            .ToArray();
        return new NativeSlashCommandPolicy(
            NativeSlashCommandCatalog.CreateBlockedCommands(commands, emoteCommandRowIds));
    }

    private static NativeTextCommandDefinition MergeLocalizedCommandRows(
        IGrouping<uint, NativeTextCommandDefinition> rows)
    {
        var parameterRows = rows.Select(row => row.ParameterRowId).Distinct().ToArray();
        if (parameterRows.Length != 1)
            throw new InvalidOperationException(
                $"Current localized FFXIV command data disagrees on the parameter schema for row {rows.Key}; refusing native command execution.");
        return new NativeTextCommandDefinition(
            rows.Key,
            parameterRows[0],
            rows.SelectMany(row => row.Aliases).ToArray());
    }

    private void OpenWindow() => RequestWindowOpen();

    private static IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor> GetCaptureSurfaces() =>
    [
        new("bridge.main-window", "Dalamud Agent Bridge window", 10, IsDefault: true),
    ];

    private void RequestWindowOpen()
    {
        WindowOpen = true;
        RequestWindowCollapsed(false);
    }

    private void RequestWindowCollapsed(bool collapsed) =>
        Interlocked.Exchange(ref requestedCollapsedState, collapsed ? 1 : 2);

    private void RestoreCaptureCollapseState(bool wasOpen, bool wasCollapsed)
    {
        if (!wasOpen)
        {
            Interlocked.Exchange(ref requestedCollapsedState, 0);
            Volatile.Write(ref windowCollapsedState, 0);
            return;
        }

        RequestWindowCollapsed(wasCollapsed);
    }

    private void Draw()
    {
        reviewRegistry.BeginFrame();
        AgentBridgeUiReviewFrame? frame = null;
        try { DrawCore(); }
        finally
        {
            frame = reviewRegistry.EndFrame();
            viewportCapture.RenderPendingWindowCapture();
        }
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
        ImGui.TextWrapped("This is the in-game connector for the loopback-only Dalamud Agent Bridge utility. It does not expose a network listener or credentials.");
        ImGui.Spacing();
        DrawRow("Process", Environment.ProcessId.ToString());
        DrawRow("Character", playerState.CharacterName ?? "Unavailable");
        DrawRow("World", playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : "Unavailable");
        DrawRow("Bridge", "Authenticated local named pipe (current user)");
        DrawRow("Screenshots", configuration.EnableScreenshots ? "Enabled — encrypted one-time handoff" : "Disabled");
        ImGui.Spacing();
        ImGui.TextUnformatted("Permissions");
        ImGui.Separator();
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
            arguments: null,
            surfaceId: "bridge.main-window",
            mutating: true,
            completionOperationKind: null,
            _ =>
            {
                ToggleScreenshotHandoff();
                return AgentBridgeUiActionResult.Ok("Screenshot handoff setting toggled.");
            });
        ImGui.Spacing();
        ImGui.TextDisabled("Capture is only available through the locally authenticated utility. This standalone plugin provides its own reviewed capture surface.");
        captureRegion = new AgentBridgeViewportRegion(
            ImGui.GetWindowPos(),
            ImGui.GetWindowSize(),
            ImGui.GetMainViewport().Pos,
            ImGui.GetMainViewport().Size,
            DateTimeOffset.UtcNow)
        {
            ViewportId = ImGui.GetMainViewport().ID,
        };
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
        client = CreateClientSnapshot(),
        reviewFrameId = reviewRegistry.Snapshot().FrameId,
        capabilities = new[] { "open-main-window", "present-surface", "get-plugin-surfaces", "begin-plugin-surface-presentation", "restore-plugin-surface-presentation", "capture-screen", "full-viewport-capture", "get-control-surface", "get-control", "invoke-control", "capture-presentation-transaction", "get-login-ui", "begin-login", "list-plugins", "enable-plugin", "disable-plugin", "install-plugin", "install-dev-plugin", "get-client-snapshot", "send-chat", "get-chat-log" },
        screenshotsEnabled = configuration.EnableScreenshots,
    };

    private object CreateLoginSnapshot()
    {
        string[] addonNames =
        [
            "_TitleMenu", "TitleDCWorldMap", "TitleConnect", "_CharaSelectWorldServer",
            "_CharaSelectListMenu", "_CharaSelectReturn", "SelectYesno", "SelectOk", "_TextError",
            "LobbyDKTWorldList", "LobbyWKTCheckHome", "NowLoading",
        ];
        return new
        {
            capturedAtUtc = DateTimeOffset.UtcNow,
            playerAvailable = !string.IsNullOrWhiteSpace(playerState.CharacterName),
            addons = addonNames.Select(renderedTextActions.CaptureVisibleText).ToArray(),
            provenance = "RenderedAddon",
        };
    }

    private object CreateClientSnapshot()
    {
        var player = objectTable[0];
        if (player is null)
            return new { available = false };
        var position = player.Position;
        var bells = objectTable
            .Where(o => o is not null && string.Equals(o.Name.TextValue, "Summoning Bell", StringComparison.OrdinalIgnoreCase))
            .Select(o => new
            {
                entityId = o.EntityId,
                x = o.Position.X,
                y = o.Position.Y,
                z = o.Position.Z,
                distance = Vector3.Distance(position, o.Position),
            })
            .OrderBy(b => b.distance)
            .ToArray();
        return new
        {
            available = true,
            characterName = playerState.CharacterName ?? "Unavailable",
            territoryType = clientState.TerritoryType,
            mapId = clientState.MapId,
            x = position.X,
            y = position.Y,
            z = position.Z,
            summoningBells = bells,
            nearestBellDistance = bells.Length > 0 ? bells[0].distance : (float?)null,
        };
    }

    private LifestreamLoginSubmissionResult BeginLogin(string target)
    {
        var separator = target.LastIndexOf('@');
        if (separator <= 0 || separator == target.Length - 1)
            return new(false, "InvalidRequest", "Target must be Character Name@Home World.");
        if (!LifestreamLoginRequest.TryCreate(target[..separator], target[(separator + 1)..], out var request, out var error))
            return new(false, "InvalidRequest", error);
        return lifestreamLogin.TryBegin(request!);
    }
}
