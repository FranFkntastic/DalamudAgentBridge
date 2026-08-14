using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Travel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SharedAgentBridgeHost = Franthropy.Dalamud.AgentBridge.AgentBridgeHost;

namespace DalamudAgentBridge.Plugin;

/// <summary>Product policy and semantic commands layered on Franthropy's shared authenticated host.</summary>
public sealed class AgentBridgeHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly Configuration configuration;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly Func<object> createSnapshot;
    private readonly Func<object> createSituation;
    private readonly Func<NavigationSnapshot> getNavigation;
    private readonly Func<NavigationPointRequest, NavigationSubmissionResult> beginNavigation;
    private readonly Func<string?, NavigationSubmissionResult> cancelNavigation;
    private readonly Func<SpecialistCatalogSnapshot> getSpecialists;
    private readonly Func<string, JsonElement?, SpecialistSubmissionResult> beginSpecialist;
    private readonly Func<string?, SpecialistSubmissionResult> cancelSpecialist;
    private readonly Func<object> createControlSurface;
    private readonly Func<string, AgentBridgeUiControlReview> reviewControl;
    private readonly Func<string, long, AgentBridgeUiControlInvocation> invokeControl;
    private readonly Action openWindow;
    private readonly Func<IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor>> getCaptureSurfaces;
    private readonly Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation;
    private readonly Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport;
    private readonly Func<string, CancellationToken, Task<AgentBridgeCaptureReceipt>> capturePluginSurface;
    private readonly Func<string, CancellationToken, Task<AgentBridgePluginWindowMeasurement>> measurePluginSurfaceWindow;
    private readonly Func<object> createPluginSnapshot;
    private readonly Func<string?, AgentBridgePluginSurfaceCatalog> createPluginSurfaceCatalog;
    private readonly Func<string, AgentBridgePluginSurfacePresentationReceipt> beginPluginSurfacePresentation;
    private readonly Func<string, AgentBridgePluginSurfacePresentationResult> restorePluginSurfacePresentation;
    private readonly Func<string, ReflectedPluginWindowInputSequence, CancellationToken, Task<ReflectedPluginWindowInputReceipt>> interactPluginSurface;
    private readonly Func<string, bool, bool?, CancellationToken, Task<object>> setPluginEnabled;
    private readonly Func<string, CancellationToken, Task<object>> installPlugin;
    private readonly Func<string, CancellationToken, Task<object>> installDevPlugin;
    private readonly Func<object> createLoginSnapshot;
    private readonly Func<object> createCharacterProvisioningSnapshot;
    private readonly Func<string, LifestreamLoginSubmissionResult> beginLogin;
    private readonly Func<string, SlashCommandSubmission> sendChatLine;
    private readonly Func<long?, int?, ChatLogRead> readChatLog;
    private readonly AgentBridgeCommandRouter router = new();
    private readonly AgentBridgeSurfaceRegistry surfaceRegistry = new();
    private readonly SharedAgentBridgeHost host;
    private readonly AgentBridgeRuntimeIdentity runtimeIdentity;
    private readonly (string Id, string Alias) profile;
    private readonly Func<IReadOnlyList<AgentBridgeActionDescriptor>> getActionCatalog;
    private readonly Func<long> getActionCatalogRevision;
    private readonly string pipeName;

    public AgentBridgeHost(
        Configuration configuration,
        string pluginInternalName,
        string configDirectory,
        string mainDllPath,
        Func<Action, Task> dispatchOnFramework,
        Func<object> createSnapshot,
        Func<object> createSituation,
        Func<NavigationSnapshot> getNavigation,
        Func<NavigationPointRequest, NavigationSubmissionResult> beginNavigation,
        Func<string?, NavigationSubmissionResult> cancelNavigation,
        Func<SpecialistCatalogSnapshot> getSpecialists,
        Func<string, JsonElement?, SpecialistSubmissionResult> beginSpecialist,
        Func<string?, SpecialistSubmissionResult> cancelSpecialist,
        Func<object> createControlSurface,
        Func<string, AgentBridgeUiControlReview> reviewControl,
        Func<string, long, AgentBridgeUiControlInvocation> invokeControl,
        Action openWindow,
        Func<IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor>> getCaptureSurfaces,
        Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation,
        Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport,
        Func<string, CancellationToken, Task<AgentBridgeCaptureReceipt>> capturePluginSurface,
        Func<string, CancellationToken, Task<AgentBridgePluginWindowMeasurement>> measurePluginSurfaceWindow,
        Func<object> createPluginSnapshot,
        Func<string?, AgentBridgePluginSurfaceCatalog> createPluginSurfaceCatalog,
        Func<string, AgentBridgePluginSurfacePresentationReceipt> beginPluginSurfacePresentation,
        Func<string, AgentBridgePluginSurfacePresentationResult> restorePluginSurfacePresentation,
        Func<string, ReflectedPluginWindowInputSequence, CancellationToken, Task<ReflectedPluginWindowInputReceipt>> interactPluginSurface,
        Func<string, bool, bool?, CancellationToken, Task<object>> setPluginEnabled,
        Func<string, CancellationToken, Task<object>> installPlugin,
        Func<string, CancellationToken, Task<object>> installDevPlugin,
        Func<object> createLoginSnapshot,
        Func<object> createCharacterProvisioningSnapshot,
        Func<string, LifestreamLoginSubmissionResult> beginLogin,
        Func<IReadOnlyList<AgentBridgeActionDescriptor>> getActionCatalog,
        Func<long> getActionCatalogRevision,
        Func<string, SlashCommandSubmission> sendChatLine,
        Func<long?, int?, ChatLogRead> readChatLog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInternalName);
        this.configuration = configuration;
        this.dispatchOnFramework = dispatchOnFramework;
        this.createSnapshot = createSnapshot;
        this.createSituation = createSituation;
        this.getNavigation = getNavigation;
        this.beginNavigation = beginNavigation;
        this.cancelNavigation = cancelNavigation;
        this.getSpecialists = getSpecialists;
        this.beginSpecialist = beginSpecialist;
        this.cancelSpecialist = cancelSpecialist;
        this.createControlSurface = createControlSurface;
        this.reviewControl = reviewControl;
        this.invokeControl = invokeControl;
        this.openWindow = openWindow;
        this.getCaptureSurfaces = getCaptureSurfaces;
        this.beginCapturePresentation = beginCapturePresentation;
        this.completeCapturePresentation = completeCapturePresentation;
        this.cancelCapturePresentation = cancelCapturePresentation;
        this.captureViewport = captureViewport;
        this.capturePluginSurface = capturePluginSurface;
        this.measurePluginSurfaceWindow = measurePluginSurfaceWindow;
        this.createPluginSnapshot = createPluginSnapshot;
        this.createPluginSurfaceCatalog = createPluginSurfaceCatalog;
        this.beginPluginSurfacePresentation = beginPluginSurfacePresentation;
        this.restorePluginSurfacePresentation = restorePluginSurfacePresentation;
        this.interactPluginSurface = interactPluginSurface;
        this.setPluginEnabled = setPluginEnabled;
        this.installPlugin = installPlugin;
        this.installDevPlugin = installDevPlugin;
        this.createLoginSnapshot = createLoginSnapshot;
        this.createCharacterProvisioningSnapshot = createCharacterProvisioningSnapshot;
        this.beginLogin = beginLogin;
        this.getActionCatalog = getActionCatalog;
        this.getActionCatalogRevision = getActionCatalogRevision;
        this.sendChatLine = sendChatLine;
        this.readChatLog = readChatLog;
        pipeName = $"{pluginInternalName}.{Environment.ProcessId}";
        profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(configDirectory);
        runtimeIdentity = AgentBridgeRuntimeIdentity.FromAssembly(pluginInternalName, Assembly.GetExecutingAssembly(), mainDllPath);
        surfaceRegistry.Register(
            new AgentBridgeReviewSurfaceDescriptor("bridge.main-window", "Dalamud Agent Bridge window", "present-surface", "bridge.main-window", 10),
            openWindow);
        RegisterCommands();
        host = new SharedAgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = configDirectory,
            PluginInstanceId = configuration.PluginInstanceId,
            PipeName = pipeName,
            GetProtectedAccessToken = () => configuration.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value => configuration.AgentBridgeProtectedAccessToken = value,
            SaveConfiguration = configuration.Save,
            CreateManifest = CreateManifest,
            HandleRequestAsync = router.HandleAsync,
            EnableAudit = true,
            RequestTimeout = TimeSpan.FromSeconds(15),
        });
    }

    private AgentBridgeManifest CreateManifest() => new(
            2,
            runtimeIdentity,
            profile.Id,
            profile.Alias,
            $"{runtimeIdentity.PluginInternalName}.snapshot.v2",
            [
                new("snapshot"), new("reviewed-actions"), new("encrypted-capture"),
                new("plugin-lifecycle"), new("plugin-install"), new("plugin-dev-install"), new("plugin-surface-inventory"), new("reversible-plugin-surface-presentation"), new("reflected-plugin-surface-input"), new("pre-login"), new("character-provisioning-observation"), new("chat", 2), new("chat-log"),
                new("situation", 2), new("navigation"), new("specialist-cockpit"),
            ],
            surfaceRegistry.Snapshot(),
            getCaptureSurfaces(),
            getActionCatalog(),
            surfaceRegistry.CatalogRevision + getActionCatalogRevision());

    public string PipeName => pipeName;

    public void Start() => host.Start();

    public void Dispose() => host.Dispose();

    private void RegisterCommands()
    {
        string[] commands =
        [
            "get-snapshot", "get-client-snapshot", "get-control-surface", "get-control", "invoke-control", "get-review-surfaces",
            "open-main-window", "present-surface", "get-capture-surfaces", "get-login-ui", "begin-login", "list-plugins",
            "get-plugin-surfaces",
            "begin-plugin-surface-presentation", "restore-plugin-surface-presentation", "interact-plugin-surface",
            "enable-plugin", "disable-plugin", "install-plugin", "install-dev-plugin", "begin-capture-presentation", "complete-capture-presentation",
            "cancel-capture-presentation", "capture-screen",
            "capture-plugin-surface", "measure-plugin-surface-window",
            "get-situation", "get-navigation", "navigate-to", "cancel-navigation",
            "get-specialists", "start-specialist", "cancel-specialist",
            "send-chat", "get-chat-log",
        ];
        foreach (var command in commands)
            router.Register(command, HandleProductRequestAsync);
        new CharacterProvisioningBridgeCommand(createCharacterProvisioningSnapshot, action => OnFrameworkAsync(action)).Register(router);
    }

    private async ValueTask<AgentBridgeResponse> HandleProductRequestAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "get-snapshot":
                return AgentBridgeResponse.Ok("Snapshot captured.", await OnFrameworkAsync(createSnapshot).ConfigureAwait(false));
            case "get-client-snapshot":
                return AgentBridgeResponse.Ok("Client snapshot captured.", await OnFrameworkAsync(createSnapshot).ConfigureAwait(false));
            case "get-situation":
                return AgentBridgeResponse.Ok("Current character situation captured.", await OnFrameworkAsync(createSituation).ConfigureAwait(false));
            case "get-navigation":
                return AgentBridgeResponse.Ok("Navigation state captured.", await OnFrameworkAsync(getNavigation).ConfigureAwait(false));
            case "navigate-to":
                var validation = NavigationRequestPolicy.Validate(request.Arguments);
                if (!validation.Success || validation.Request is null)
                    return new AgentBridgeResponse { Success = false, Message = validation.Message, Receipt = validation };
                var navigation = await OnFrameworkAsync(() => beginNavigation(validation.Request)).ConfigureAwait(false);
                return navigation.Success
                    ? AgentBridgeResponse.Ok(navigation.Message, navigation, navigation.Navigation.OperationId)
                    : new AgentBridgeResponse { Success = false, Message = navigation.Message, Receipt = navigation };
            case "cancel-navigation":
                var cancelled = await OnFrameworkAsync(() => cancelNavigation(request.OperationId)).ConfigureAwait(false);
                return cancelled.Success
                    ? AgentBridgeResponse.Ok(cancelled.Message, cancelled, cancelled.Navigation.OperationId)
                    : new AgentBridgeResponse { Success = false, Message = cancelled.Message, Receipt = cancelled };
            case "get-specialists":
                return AgentBridgeResponse.Ok("Specialist capability catalog captured.", await OnFrameworkAsync(getSpecialists).ConfigureAwait(false));
            case "start-specialist":
                if (string.IsNullOrWhiteSpace(request.Target))
                    return AgentBridgeResponse.Fail("A reviewed specialist capability id is required.");
                var specialist = await OnFrameworkAsync(() => beginSpecialist(request.Target, request.Arguments)).ConfigureAwait(false);
                return specialist.Success
                    ? AgentBridgeResponse.Ok(specialist.Message, specialist, specialist.Operation.OperationId)
                    : new AgentBridgeResponse { Success = false, Message = specialist.Message, Receipt = specialist };
            case "cancel-specialist":
                var specialistCancelled = await OnFrameworkAsync(() => cancelSpecialist(request.OperationId)).ConfigureAwait(false);
                return specialistCancelled.Success
                    ? AgentBridgeResponse.Ok(specialistCancelled.Message, specialistCancelled, specialistCancelled.Operation.OperationId)
                    : new AgentBridgeResponse { Success = false, Message = specialistCancelled.Message, Receipt = specialistCancelled };
            case "get-control-surface":
                return AgentBridgeResponse.Ok("Control surface captured.", await OnFrameworkAsync(createControlSurface).ConfigureAwait(false));
            case "get-review-surfaces":
                return AgentBridgeResponse.Ok("Review surfaces captured.", surfaceRegistry.Snapshot());
            case "get-plugin-surfaces":
                return AgentBridgeResponse.Ok(
                    "Plugin UI surface inventory captured.",
                    await OnFrameworkAsync(() => createPluginSurfaceCatalog(request.Target)).ConfigureAwait(false));
            case "begin-plugin-surface-presentation":
                if (string.IsNullOrWhiteSpace(request.Target))
                    return AgentBridgeResponse.Fail("A reversible reflected surface ID is required.");
                try
                {
                    return AgentBridgeResponse.Ok(
                        "Plugin surface presented under a short-lived reversible lease.",
                        await OnFrameworkAsync(() => beginPluginSurfacePresentation(request.Target)).ConfigureAwait(false));
                }
                catch (InvalidOperationException exception)
                {
                    return AgentBridgeResponse.Fail($"Plugin surface presentation failed: {exception.Message}");
                }
            case "restore-plugin-surface-presentation":
                if (string.IsNullOrWhiteSpace(request.TransactionId))
                    return AgentBridgeResponse.Fail("A presentation transaction identifier is required.");
                var restoredPresentation = await OnFrameworkAsync(() => restorePluginSurfacePresentation(request.TransactionId)).ConfigureAwait(false);
                return restoredPresentation.Success
                    ? AgentBridgeResponse.Ok(restoredPresentation.Message, restoredPresentation)
                    : new AgentBridgeResponse { Success = false, Message = restoredPresentation.Message, Receipt = restoredPresentation };
            case "interact-plugin-surface":
                if (!configuration.EnableSurfaceInput)
                    return AgentBridgeResponse.Fail("Reflected plugin surface input is disabled in the in-game DAB settings.");
                if (string.IsNullOrWhiteSpace(request.TransactionId))
                    return AgentBridgeResponse.Fail("A current plugin surface presentation transaction is required.");
                if (request.Arguments is not { } inputArguments)
                    return AgentBridgeResponse.Fail("A bounded plugin surface input sequence is required.");
                ReflectedPluginWindowInputSequence? inputSequence;
                try
                {
                    inputSequence = inputArguments.Deserialize<ReflectedPluginWindowInputSequence>(JsonOptions);
                }
                catch (JsonException exception)
                {
                    return AgentBridgeResponse.Fail($"Plugin surface input is invalid: {exception.Message}");
                }
                if (inputSequence is null)
                    return AgentBridgeResponse.Fail("The plugin surface input sequence was empty.");
                try
                {
                    return AgentBridgeResponse.Ok(
                        "Bounded input was delivered to the leased plugin ImGui window.",
                        await interactPluginSurface(request.TransactionId, inputSequence, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    return AgentBridgeResponse.Fail("Plugin surface input was cancelled or timed out.");
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return AgentBridgeResponse.Fail($"Plugin surface input failed: {exception.Message}");
                }
            case "get-control":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A control ID is required.");
                var review = await OnFrameworkAsync(() => reviewControl(request.Target)).ConfigureAwait(false);
                return review.Control is null
                    ? new AgentBridgeResponse { Success = false, Message = "The requested control is not rendered.", Receipt = review }
                    : AgentBridgeResponse.Ok("Reviewed control captured.", review);
            case "invoke-control":
                if (string.IsNullOrWhiteSpace(request.Target) || request.FrameId is not { } frameId)
                    return AgentBridgeResponse.Fail("A control ID and rendered frame ID are required.");
                var invocation = await OnFrameworkAsync(() => invokeControl(request.Target, frameId)).ConfigureAwait(false);
                return invocation.Success
                    ? AgentBridgeResponse.Ok(invocation.Message, invocation.Frame)
                    : new AgentBridgeResponse { Success = false, Message = invocation.Message, Receipt = invocation.Frame };
            case "open-main-window":
                await dispatchOnFramework(openWindow).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Agent Bridge window opened.");
            case "present-surface":
                if (string.IsNullOrWhiteSpace(request.Target))
                    return AgentBridgeResponse.Fail("A registered surface ID is required.");
                var presented = false;
                await dispatchOnFramework(() => presented = surfaceRegistry.TryPresent(request.Target)).ConfigureAwait(false);
                return presented
                    ? AgentBridgeResponse.Ok("Registered surface presented.")
                    : AgentBridgeResponse.Fail("The requested surface is not registered.");
            case "get-capture-surfaces":
                return AgentBridgeResponse.Ok("Capture surfaces captured.", getCaptureSurfaces());
            case "get-login-ui":
                return AgentBridgeResponse.Ok("Rendered title and login UI captured without requiring a local player.", await OnFrameworkAsync(createLoginSnapshot).ConfigureAwait(false));
            case "begin-login":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A Character Name@Home World target is required.");
                var login = await OnFrameworkAsync(() => beginLogin(request.Target)).ConfigureAwait(false);
                return login.Success
                    ? AgentBridgeResponse.Ok("Login submitted; rendered and logged-in postconditions remain required.", login)
                    : new AgentBridgeResponse { Success = false, Message = login.Message, Receipt = login };
            case "list-plugins":
                return AgentBridgeResponse.Ok("Installed plugin state captured.", await OnFrameworkAsync(createPluginSnapshot).ConfigureAwait(false));
            case "enable-plugin":
            case "disable-plugin":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A plugin internal name is required.");
                try
                {
                    var enable = request.Command == "enable-plugin";
                    bool? isDev = null;
                    if (request.Arguments is { } arguments && arguments.TryGetProperty("isDev", out var isDevElement))
                    {
                        if (isDevElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return AgentBridgeResponse.Fail("Plugin selector 'isDev' must be a boolean.");
                        isDev = isDevElement.GetBoolean();
                    }
                    var receipt = await setPluginEnabled(request.Target, enable, isDev, cancellationToken).ConfigureAwait(false);
                    return AgentBridgeResponse.Ok(enable ? "Plugin enabled." : "Plugin disabled.", receipt);
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or OperationCanceledException)
                {
                    return AgentBridgeResponse.Fail($"Plugin lifecycle change failed: {exception.Message}");
                }
            case "install-plugin":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A plugin internal name is required.");
                try
                {
                    return AgentBridgeResponse.Ok("Plugin installed and loaded.", await installPlugin(request.Target, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or OperationCanceledException)
                {
                    return AgentBridgeResponse.Fail($"Plugin install failed: {exception.Message}");
                }
            case "install-dev-plugin":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A plugin internal name is required.");
                try
                {
                    return AgentBridgeResponse.Ok("Dev plugin installed and loaded.", await installDevPlugin(request.Target, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or OperationCanceledException)
                {
                    return AgentBridgeResponse.Fail($"Dev plugin install failed: {exception.Message}");
                }
            case "begin-capture-presentation":
                return await BeginCapturePresentationAsync(request, cancellationToken).ConfigureAwait(false);
            case "complete-capture-presentation":
            case "cancel-capture-presentation":
                if (string.IsNullOrWhiteSpace(request.TransactionId)) return AgentBridgeResponse.Fail("A capture transaction identifier is required.");
                var result = await OnFrameworkAsync(() => request.Command == "complete-capture-presentation"
                    ? completeCapturePresentation(request.TransactionId)
                    : cancelCapturePresentation(request.TransactionId)).ConfigureAwait(false);
                return result.Success ? AgentBridgeResponse.Ok(result.Message, result) : AgentBridgeResponse.Fail(result.Message);
            case "capture-screen":
                if (!configuration.EnableScreenshots) return AgentBridgeResponse.Fail("Agent Bridge screenshots are disabled in the in-game plugin settings.");
                if (!string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("This independent bridge has no plugin-specific target surfaces.");
                try { return AgentBridgeResponse.Ok("Rendered viewport captured.", await captureViewport(request.FullViewport, cancellationToken).ConfigureAwait(false)); }
                catch (OperationCanceledException) { return AgentBridgeResponse.Fail("Rendered viewport capture timed out."); }
                catch (Exception exception) { return AgentBridgeResponse.Fail($"Rendered viewport capture failed: {exception.Message}"); }
            case "capture-plugin-surface":
                if (!configuration.EnableScreenshots) return AgentBridgeResponse.Fail("Agent Bridge screenshots are disabled in the in-game plugin settings.");
                if (string.IsNullOrWhiteSpace(request.TransactionId))
                    return AgentBridgeResponse.Fail("An active plugin surface presentation transaction is required.");
                try
                {
                    return AgentBridgeResponse.Ok(
                        "Presented plugin window captured.",
                        await capturePluginSurface(request.TransactionId, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) { return AgentBridgeResponse.Fail("Presented plugin surface capture timed out."); }
                catch (Exception exception) { return AgentBridgeResponse.Fail($"Presented plugin surface capture failed: {exception.Message}"); }
            case "measure-plugin-surface-window":
                if (string.IsNullOrWhiteSpace(request.TransactionId))
                    return AgentBridgeResponse.Fail("An active plugin surface presentation transaction is required.");
                try
                {
                    return AgentBridgeResponse.Ok(
                        "Presented plugin platform window resolved.",
                        await measurePluginSurfaceWindow(request.TransactionId, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) { return AgentBridgeResponse.Fail("Presented plugin platform window measurement timed out."); }
                catch (Exception exception) { return AgentBridgeResponse.Fail($"Presented plugin platform window measurement failed: {exception.Message}"); }
            case "send-chat":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A chat line is required.");
                var chatLine = request.Target.Trim();
                if (chatLine.IndexOfAny(['\r', '\n']) >= 0) return AgentBridgeResponse.Fail("A chat line must be a single line.");
                if (!chatLine.StartsWith('/')) return AgentBridgeResponse.Fail("send-chat only accepts slash commands; plain chat text is never sent.");
                var submission = await OnFrameworkAsync(() => sendChatLine(chatLine)).ConfigureAwait(false);
                return submission.Submitted
                    ? AgentBridgeResponse.Ok("Slash command submitted.", new
                    {
                        line = chatLine,
                        route = submission.Route,
                        submission.HandledByPluginCommand,
                        submission.ForwardedToNativeCommand,
                    })
                    : AgentBridgeResponse.Fail(submission.Message);
            case "get-chat-log":
                var (cursor, limit) = ParseChatLogQuery(request.Arguments);
                return AgentBridgeResponse.Ok("Chat log entries captured.", readChatLog(cursor, limit));
            default:
                return AgentBridgeResponse.Fail("Bridge command is not allowed.");
        }
    }

    private async ValueTask<AgentBridgeResponse> BeginCapturePresentationAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        if (!configuration.EnableScreenshots) return AgentBridgeResponse.Fail("Agent Bridge screenshots are disabled in the in-game plugin settings.");
        if (string.IsNullOrWhiteSpace(request.Target) || !getCaptureSurfaces().Any(surface => surface.Id == request.Target))
            return AgentBridgeResponse.Fail("The requested capture presentation target is not registered.");
        AgentBridgeUiCaptureTransactionHandle? handle = null;
        try
        {
            handle = await OnFrameworkAsync(() => beginCapturePresentation(request.Target)).ConfigureAwait(false);
            return AgentBridgeResponse.Ok("Capture presentation rendered and ready.", await handle.Ready.WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            if (handle is not null)
                await dispatchOnFramework(() => cancelCapturePresentation(handle.TransactionId)).ConfigureAwait(false);
            return AgentBridgeResponse.Fail($"Capture presentation failed: {exception.Message}");
        }
    }

    private static (long? Cursor, int? Limit) ParseChatLogQuery(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } query)
            return (null, null);
        long? cursor = query.TryGetProperty("cursor", out var cursorValue) && cursorValue.ValueKind == JsonValueKind.Number && cursorValue.TryGetInt64(out var parsedCursor)
            ? parsedCursor
            : null;
        int? limit = query.TryGetProperty("limit", out var limitValue) && limitValue.ValueKind == JsonValueKind.Number && limitValue.TryGetInt32(out var parsedLimit)
            ? parsedLimit
            : null;
        return (cursor, limit);
    }

    private async Task<T> OnFrameworkAsync<T>(Func<T> action)
    {
        T? result = default;
        await dispatchOnFramework(() => result = action()).ConfigureAwait(false);
        return result!;
    }
}

public sealed record SlashCommandSubmission(
    bool Submitted,
    bool HandledByPluginCommand,
    bool ForwardedToNativeCommand,
    string Route,
    string Message)
{
    public static SlashCommandSubmission PluginCommand() =>
        new(true, true, false, "plugin", "Dalamud plugin command handled.");

    public static SlashCommandSubmission NativeCommand() =>
        new(true, false, true, "native", "Native game command submitted.");

    public static SlashCommandSubmission Rejected(string message) =>
        new(false, false, false, "rejected", message);
}
