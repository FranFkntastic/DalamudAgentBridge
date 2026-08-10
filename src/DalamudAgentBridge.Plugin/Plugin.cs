using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Characters;
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
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IChatGui chatGui;
    private readonly ICondition condition;
    private readonly ITargetManager targetManager;
    private readonly IPartyList partyList;
    private readonly IGameGui gameGui;
    private readonly ChatLogBuffer chatLogBuffer = new();
    private readonly Configuration configuration;
    private readonly AgentBridgeViewportCaptureService viewportCapture;
    private readonly AgentBridgeHost bridgeHost;
    private readonly DalamudPluginLifecycleService pluginLifecycle;
    private readonly DalamudPluginInstallService pluginInstall;
    private readonly DalamudPluginDevInstallService pluginDevInstall;
    private readonly DalamudPluginSurfaceDiscoveryService pluginSurfaceDiscovery;
    private readonly DalamudPluginSurfacePresentationService pluginSurfacePresentation;
    private readonly ReflectedPluginWindowInputController pluginSurfaceInput;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry = new();
    private readonly AgentBridgeUiCaptureTransactionManager captureTransactions;
    private readonly DalamudRenderedUiTextActionDispatcher renderedTextActions;
    private readonly DalamudLifestreamLogin lifestreamLogin;
    private readonly GameplayControlLease gameplayControl = new();
    private readonly NavigationCoordinator navigation;
    private readonly SpecialistOperationCoordinator specialists;
    private readonly NativeSlashCommandPolicy nativeSlashCommandPolicy;
    private readonly DalamudSharedObservationHost? sharedObservationHost;
    private readonly string commandName;
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
        ICondition condition,
        ITargetManager targetManager,
        IPartyList partyList,
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
        this.condition = condition;
        this.targetManager = targetManager;
        this.partyList = partyList;
        this.gameGui = gameGui;
        commandName = string.Equals(pluginInterface.Manifest.InternalName, "DalamudAgentBridge", StringComparison.OrdinalIgnoreCase)
            ? "/dab"
            : "/dab-ui-audit";
        nativeSlashCommandPolicy = CreateNativeSlashCommandPolicy(dataManager);
        this.chatGui.ChatMessage += OnChatMessage;
        renderedTextActions = new(gameGui);
        lifestreamLogin = new(pluginInterface);
        navigation = new NavigationCoordinator(
            framework,
            clientState,
            objectTable,
            condition,
            new DalamudVNavmeshTravel(pluginInterface),
            gameplayControl);
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        configuration.Initialize(pluginInterface);
        specialists = new SpecialistOperationCoordinator(SpecialistAdapters.Create(pluginInterface), gameplayControl);
        framework.Update += OnFrameworkUpdate;
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
        pluginSurfacePresentation = new DalamudPluginSurfacePresentationService(pluginSurfaceDiscovery, framework, TimeSpan.FromSeconds(30));
        pluginSurfaceInput = new ReflectedPluginWindowInputController(pluginSurfacePresentation.GetActiveTarget);
        bridgeHost = new AgentBridgeHost(
            configuration,
            pluginInterface.Manifest.InternalName,
            pluginInterface.GetPluginConfigDirectory(),
            pluginInterface.AssemblyLocation.FullName,
            action => framework.RunOnTick(action),
            CreateSnapshot,
            CreateSituationSnapshot,
            navigation.Observe,
            request => navigation.TryBegin(request, configuration.EnableNavigation),
            navigation.TryCancel,
            () => specialists.Observe(configuration.EnableSpecialistAutomation),
            (capabilityId, arguments) => specialists.TryBegin(capabilityId, arguments, configuration.EnableSpecialistAutomation),
            specialists.TryCancel,
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
            pluginSurfaceInput.SubmitAsync,
            async (internalName, enabled, cancellationToken) =>
                await pluginLifecycle.SetEnabledAsync(internalName, enabled, cancellationToken).ConfigureAwait(false),
            async (internalName, cancellationToken) =>
                await pluginInstall.InstallAsync(internalName, cancellationToken).ConfigureAwait(false),
            async (internalName, cancellationToken) =>
                await pluginDevInstall.InstallDevAsync(internalName, cancellationToken).ConfigureAwait(false),
            CreateLoginSnapshot,
            CreateCharacterProvisioningSnapshot,
            BeginLogin,
            reviewRegistry.ActionCatalog,
            () => reviewRegistry.CatalogRevision,
            SendChatLine,
            chatLogBuffer.Read);
        bridgeHost.Start();
        commandManager.AddHandler(commandName, new CommandInfo(OnCommand)
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
                PluginName = pluginInterface.Manifest.InternalName,
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
        framework.Update -= OnFrameworkUpdate;
        specialists.Dispose();
        navigation.Dispose();
        chatGui.ChatMessage -= OnChatMessage;
        viewportCapture.Dispose();
        pluginSurfaceInput.Dispose();
        pluginSurfacePresentation.Dispose();
        sharedObservationHost?.Dispose();
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        pluginInterface.UiBuilder.OpenMainUi -= OpenWindow;
        commandManager.RemoveHandler(commandName);
    }

    private void OnFrameworkUpdate(IFramework _) => specialists.Tick();

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
            pluginSurfaceInput.RenderFrame();
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
        ImGui.SetNextWindowSize(new Vector2(620, 340), ImGuiCond.FirstUseEver);
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
        DrawRow("Surface input", configuration.EnableSurfaceInput ? "Enabled - leased ImGui plugin windows only" : "Disabled");
        DrawRow("Navigation", configuration.EnableNavigation ? "Enabled - explicit same-territory requests" : "Disabled");
        DrawRow("Specialists", configuration.EnableSpecialistAutomation ? "Enabled - reviewed plugin adapters" : "Disabled");
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
        var surfaceInputEnabled = configuration.EnableSurfaceInput;
        if (ImGui.Checkbox("Allow bounded input inside leased plugin ImGui windows", ref surfaceInputEnabled))
        {
            configuration.EnableSurfaceInput = surfaceInputEnabled;
            configuration.Save();
        }
        ImGui.TextDisabled("Surface input is normalized to one current reflected plugin window; it never emits desktop input or targets native FFXIV UI.");
        var navigationEnabled = configuration.EnableNavigation;
        if (ImGui.Checkbox("Allow explicit same-territory navigation through vnavmesh", ref navigationEnabled))
        {
            configuration.EnableNavigation = navigationEnabled;
            configuration.Save();
            if (!navigationEnabled)
                navigation.RequestPermissionRevocation();
        }
        var specialistsEnabled = configuration.EnableSpecialistAutomation;
        if (ImGui.Checkbox("Allow reviewed specialist plugins to automate gameplay", ref specialistsEnabled))
        {
            configuration.EnableSpecialistAutomation = specialistsEnabled;
            configuration.Save();
            if (!specialistsEnabled)
                specialists.RequestPermissionRevocation();
        }
        ImGui.TextDisabled("Only typed Questionable, AutoDuty, Henchman, and Lifestream adapters can act; unknown IPC remains read-only.");
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
        capabilities = new[] { "open-main-window", "present-surface", "get-plugin-surfaces", "begin-plugin-surface-presentation", "restore-plugin-surface-presentation", "interact-plugin-surface", "capture-screen", "full-viewport-capture", "get-control-surface", "get-control", "invoke-control", "capture-presentation-transaction", "get-login-ui", "get-character-provisioning", "begin-login", "list-plugins", "enable-plugin", "disable-plugin", "install-plugin", "install-dev-plugin", "get-client-snapshot", "get-situation", "navigate-to", "get-navigation", "cancel-navigation", "get-specialists", "start-specialist", "cancel-specialist", "send-chat", "get-chat-log" },
        screenshotsEnabled = configuration.EnableScreenshots,
        surfaceInputEnabled = configuration.EnableSurfaceInput,
        navigationEnabled = configuration.EnableNavigation,
        specialistAutomationEnabled = configuration.EnableSpecialistAutomation,
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

    private object CreateCharacterProvisioningSnapshot()
    {
        string[] addonNames =
        [
            "_TitleMenu",
            "_CharaSelectWorldServer",
            "_CharaSelectListMenu",
            "_CharaSelectReturn",
            "_CharaMakeRaceGender",
            "_CharaMakeTribe",
            "_CharaMakeFeature",
            "_CharaMakeBirthDay",
            "_CharaMakeGuardian",
            "_CharaMakeClassSelector",
            "_CharaMakeWorldServer",
            "_CharaMakeCharaName",
            "_CharaMakeNotice",
            "_CharaMakeProgress",
            "CharaMakeSelectYesNo",
            "_CharaMakeSelectYesNo",
            "SelectYesno",
            "SelectOk",
            "_TextError",
            "NowLoading",
        ];
        var addons = addonNames.Select(renderedTextActions.CaptureVisibleText).ToArray();
        var playerAvailable = !string.IsNullOrWhiteSpace(playerState.CharacterName);
        var gameVersion = Franthropy.Dalamud.Diagnostics.GamePatchCompatibilityGate.ReadCurrentGameVersion();
        var stage = CharacterCreationStageDetector.Detect(
            addons.Where(value => value.Available).Select(value => value.AddonName),
            playerAvailable,
            gameVersion,
            CharacterProvisioningDefaults.ApprovedGameVersion);
        return new
        {
            schemaVersion = CharacterProvisioningDefaults.SchemaVersion,
            capturedAtUtc = DateTimeOffset.UtcNow,
            gameVersion,
            approvedGameVersion = CharacterProvisioningDefaults.ApprovedGameVersion,
            playerAvailable,
            stage,
            selection = CaptureCharacterProvisioningSelection(),
            addons,
            provenance = "RenderedAddon",
        };
    }

    private unsafe CharacterProvisioningSelectionObservation CaptureCharacterProvisioningSelection()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("_CharaMakeWorldServer", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return CharacterProvisioningSelectionResolver.Resolve([]);

        var candidates = new List<CharacterProvisioningSelectionCandidate>();
        CaptureSelectedWorldLists(&addon->UldManager, candidates, new HashSet<nint>());
        return CharacterProvisioningSelectionResolver.Resolve(candidates);
    }

    private static unsafe void CaptureSelectedWorldLists(
        AtkUldManager* manager,
        List<CharacterProvisioningSelectionCandidate> candidates,
        HashSet<nint> visited)
    {
        if (manager == null || manager->NodeList == null || !visited.Add((nint)manager))
            return;
        for (var index = 0; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            var componentNode = node == null ? null : node->GetAsAtkComponentNode();
            if (componentNode == null || componentNode->Component == null)
                continue;
            if (componentNode->Component->GetComponentType() == ComponentType.List)
            {
                var list = (AtkComponentList*)componentNode->Component;
                var selectedIndex = list->SelectedItemIndex;
                if (selectedIndex >= 0 && selectedIndex < list->ListLength)
                {
                    var selectedChoice = list->ItemRendererList != null
                        ? list->ItemRendererList[selectedIndex].Label.ToString().Trim()
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(selectedChoice) && list->ItemLabels != null)
                        selectedChoice = list->ItemLabels[selectedIndex].ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(selectedChoice))
                        candidates.Add(new(selectedChoice, selectedChoice, "_CharaMakeWorldServer.AtkComponentList.SelectedItemIndex"));
                }
            }
            CaptureSelectedWorldLists(&componentNode->Component->UldManager, candidates, visited);
        }
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

    private object CreateSituationSnapshot()
    {
        var player = objectTable[0];
        var activeConditions = condition.AsReadOnlySet()
            .Select(flag => flag.ToString())
            .OrderBy(name => name)
            .ToArray();
        if (player is null)
            return new
            {
                schemaVersion = 2,
                capturedAtUtc = DateTimeOffset.UtcNow,
                available = false,
                client = DescribeClient(),
                activeConditions,
                navigation = navigation.Observe(),
                specialists = specialists.ObserveSituation(configuration.EnableSpecialistAutomation),
                provenance = "DalamudPublicApi",
            };

        var position = player.Position;
        var character = player as ICharacter;
        var battleCharacter = player as IBattleChara;
        var nearby = objectTable
            .Where(value => value is not null && value.EntityId != player.EntityId)
            .Select(value => DescribeObject(value!, position))
            .Where(value => value.Distance <= 100f)
            .OrderBy(value => value.Distance)
            .Take(48)
            .ToArray();
        var party = Enumerable.Range(0, partyList.Length)
            .Select(index => partyList[index])
            .Where(member => member is not null)
            .Select(member => new
            {
                name = member!.Name.TextValue,
                entityId = member.EntityId,
                classJobId = member.ClassJob.RowId,
                level = member.Level,
                currentHp = member.CurrentHP,
                maxHp = member.MaxHP,
                currentMp = member.CurrentMP,
                maxMp = member.MaxMP,
                territoryType = member.Territory.RowId,
                x = member.Position.X,
                y = member.Position.Y,
                z = member.Position.Z,
                distance = Vector3.Distance(position, member.Position),
            })
            .ToArray();
        string[] decisionAddons =
        [
            "_TargetInfo", "_TargetInfoMainTarget", "_FocusTargetInfo", "_PartyList",
            "Talk", "SelectString", "SelectIconString", "SelectYesno", "SelectOk",
            "ContentsFinderConfirm", "JournalAccept", "JournalResult", "NowLoading",
            "AreaMap", "Gathering", "RecipeNote", "RetainerList", "RetainerSellList", "Shop",
        ];
        return new
        {
            schemaVersion = 2,
            capturedAtUtc = DateTimeOffset.UtcNow,
            available = true,
            client = DescribeClient(),
            character = new
            {
                name = playerState.CharacterName ?? player.Name.TextValue,
                currentWorld = playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : null,
                homeWorld = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null,
                entityId = player.EntityId,
                x = position.X,
                y = position.Y,
                z = position.Z,
                mapCoordinates = TryGetMapCoordinates(player),
                rotation = player.Rotation,
                isDead = player.IsDead,
                isTargetable = player.IsTargetable,
                classJobId = character?.ClassJob.RowId,
                level = character?.Level,
                currentHp = character?.CurrentHp,
                maxHp = character?.MaxHp,
                currentMp = character?.CurrentMp,
                maxMp = character?.MaxMp,
                currentGp = character?.CurrentGp,
                maxGp = character?.MaxGp,
                currentCp = character?.CurrentCp,
                maxCp = character?.MaxCp,
                shieldPercent = character?.ShieldPercentage,
                isCasting = battleCharacter?.IsCasting,
                castActionId = battleCharacter?.CastActionId,
                currentCastTime = battleCharacter?.CurrentCastTime,
                totalCastTime = battleCharacter?.TotalCastTime,
                statuses = battleCharacter?.StatusList.Select(status => new
                {
                    statusId = status.StatusId,
                    param = status.Param,
                    remainingSeconds = status.RemainingTime,
                    sourceId = status.SourceId,
                }).ToArray(),
            },
            target = DescribeTarget(targetManager.Target, position),
            focusTarget = DescribeTarget(targetManager.FocusTarget, position),
            activeConditions,
            party,
            nearbyObjects = nearby,
            visibleDecisionUi = decisionAddons.Select(renderedTextActions.CaptureVisibleText).ToArray(),
            recentChat = chatLogBuffer.Read(null, 20).Entries,
            navigation = navigation.Observe(),
            specialists = specialists.ObserveSituation(configuration.EnableSpecialistAutomation),
            bounds = new { nearbyRadius = 100f, nearbyLimit = 48, recentChatLimit = 20 },
            provenance = "DalamudPublicApiAndRenderedAddon",
        };
    }

    private object DescribeClient() => new
    {
        loggedIn = clientState.IsLoggedIn,
        territoryType = clientState.TerritoryType,
        mapId = clientState.MapId,
        instance = clientState.Instance,
        isPvP = clientState.IsPvP,
        isGPosing = clientState.IsGPosing,
        language = clientState.ClientLanguage.ToString(),
    };

    private static SituationObject DescribeObject(IGameObject value, Vector3 origin)
    {
        var map = TryGetMapCoordinates(value);
        return new SituationObject(
            value.Name.TextValue,
            value.EntityId,
            value.BaseId,
            value.ObjectKind.ToString(),
            value.SubKind,
            value.Position.X,
            value.Position.Y,
            value.Position.Z,
            map?.X,
            map?.Y,
            map?.Z,
            value.Rotation,
            value.HitboxRadius,
            Vector3.Distance(origin, value.Position),
            value.IsTargetable,
            value.IsDead);
    }

    private static object? DescribeTarget(IGameObject? value, Vector3 origin)
    {
        if (value is null)
            return null;
        var character = value as ICharacter;
        var battleCharacter = value as IBattleChara;
        return new
        {
            gameObject = DescribeObject(value, origin),
            currentHp = character?.CurrentHp,
            maxHp = character?.MaxHp,
            shieldPercent = character?.ShieldPercentage,
            isCasting = battleCharacter?.IsCasting,
            isCastInterruptible = battleCharacter?.IsCastInterruptible,
            castActionId = battleCharacter?.CastActionId,
            currentCastTime = battleCharacter?.CurrentCastTime,
            totalCastTime = battleCharacter?.TotalCastTime,
            statuses = battleCharacter?.StatusList.Select(status => new
            {
                statusId = status.StatusId,
                param = status.Param,
                remainingSeconds = status.RemainingTime,
                sourceId = status.SourceId,
            }).ToArray(),
        };
    }

    private static SituationCoordinates? TryGetMapCoordinates(IGameObject value)
    {
        try
        {
            var map = MapUtil.GetMapCoordinates(value, true);
            return new SituationCoordinates(map.X, map.Y, map.Z);
        }
        catch (InvalidOperationException) { return null; }
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

public sealed record SituationObject(
    string Name,
    uint EntityId,
    uint BaseId,
    string Kind,
    byte SubKind,
    float X,
    float Y,
    float Z,
    float? MapX,
    float? MapY,
    float? MapZ,
    float Rotation,
    float HitboxRadius,
    float Distance,
    bool IsTargetable,
    bool IsDead);

public sealed record SituationCoordinates(float X, float Y, float Z);
