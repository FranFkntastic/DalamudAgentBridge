using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Travel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SharedAgentBridgeHost = Franthropy.Dalamud.AgentBridge.AgentBridgeHost;

namespace DalamudAgentBridge.Plugin;

/// <summary>Product policy and semantic commands layered on Franthropy's shared authenticated host.</summary>
public sealed class AgentBridgeHost : IDisposable
{
    private readonly Configuration configuration;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly Func<object> createSnapshot;
    private readonly Func<object> createControlSurface;
    private readonly Func<string, AgentBridgeUiControlReview> reviewControl;
    private readonly Func<string, long, AgentBridgeUiControlInvocation> invokeControl;
    private readonly Action openWindow;
    private readonly Func<IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor>> getCaptureSurfaces;
    private readonly Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation;
    private readonly Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation;
    private readonly Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport;
    private readonly Func<object> createPluginSnapshot;
    private readonly Func<string, bool, CancellationToken, Task<object>> setPluginEnabled;
    private readonly Func<object> createLoginSnapshot;
    private readonly Func<string, LifestreamLoginSubmissionResult> beginLogin;
    private readonly AgentBridgeCommandRouter router = new();
    private readonly SharedAgentBridgeHost host;
    private readonly AgentBridgeManifest manifest;

    public AgentBridgeHost(
        Configuration configuration,
        string configDirectory,
        string mainDllPath,
        Func<Action, Task> dispatchOnFramework,
        Func<object> createSnapshot,
        Func<object> createControlSurface,
        Func<string, AgentBridgeUiControlReview> reviewControl,
        Func<string, long, AgentBridgeUiControlInvocation> invokeControl,
        Action openWindow,
        Func<IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor>> getCaptureSurfaces,
        Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation,
        Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation,
        Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport,
        Func<object> createPluginSnapshot,
        Func<string, bool, CancellationToken, Task<object>> setPluginEnabled,
        Func<object> createLoginSnapshot,
        Func<string, LifestreamLoginSubmissionResult> beginLogin)
    {
        this.configuration = configuration;
        this.dispatchOnFramework = dispatchOnFramework;
        this.createSnapshot = createSnapshot;
        this.createControlSurface = createControlSurface;
        this.reviewControl = reviewControl;
        this.invokeControl = invokeControl;
        this.openWindow = openWindow;
        this.getCaptureSurfaces = getCaptureSurfaces;
        this.beginCapturePresentation = beginCapturePresentation;
        this.completeCapturePresentation = completeCapturePresentation;
        this.cancelCapturePresentation = cancelCapturePresentation;
        this.captureViewport = captureViewport;
        this.createPluginSnapshot = createPluginSnapshot;
        this.setPluginEnabled = setPluginEnabled;
        this.createLoginSnapshot = createLoginSnapshot;
        this.beginLogin = beginLogin;
        var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(configDirectory);
        manifest = new AgentBridgeManifest(
            2,
            AgentBridgeRuntimeIdentity.FromAssembly("DalamudAgentBridge", Assembly.GetExecutingAssembly(), mainDllPath),
            profile.Id,
            profile.Alias,
            "DalamudAgentBridge.snapshot.v2",
            [
                new("snapshot"), new("reviewed-actions"), new("encrypted-capture"),
                new("plugin-lifecycle"), new("pre-login"),
            ],
            [new("bridge.main-window", "Dalamud Agent Bridge window", "open-main-window", "bridge.main-window", 10)],
            getCaptureSurfaces(),
            [new("bridge.screenshot-handoff", "Toggle screenshot handoff", "bridge.main-window", AgentBridgeUiControlKind.Toggle, true)]);
        RegisterCommands();
        host = new SharedAgentBridgeHost(new AgentBridgeHostOptions
        {
            ConfigDirectory = configDirectory,
            PluginInstanceId = configuration.PluginInstanceId,
            PipeName = $"DalamudAgentBridge.{Environment.ProcessId}",
            GetProtectedAccessToken = () => configuration.AgentBridgeProtectedAccessToken,
            SetProtectedAccessToken = value => configuration.AgentBridgeProtectedAccessToken = value,
            SaveConfiguration = configuration.Save,
            CreateManifest = () => manifest,
            HandleRequestAsync = router.HandleAsync,
            EnableAudit = true,
            RequestTimeout = TimeSpan.FromSeconds(15),
        });
    }

    public string PipeName => $"DalamudAgentBridge.{Environment.ProcessId}";

    public void Start() => host.Start();

    public void Dispose() => host.Dispose();

    private void RegisterCommands()
    {
        string[] commands =
        [
            "get-snapshot", "get-control-surface", "get-control", "invoke-control", "get-review-surfaces",
            "open-main-window", "get-capture-surfaces", "get-login-ui", "begin-login", "list-plugins",
            "enable-plugin", "disable-plugin", "begin-capture-presentation", "complete-capture-presentation",
            "cancel-capture-presentation", "capture-screen",
        ];
        foreach (var command in commands)
            router.Register(command, HandleProductRequestAsync);
    }

    private async ValueTask<AgentBridgeResponse> HandleProductRequestAsync(AgentBridgeRequest request, CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "get-snapshot":
                return AgentBridgeResponse.Ok("Snapshot captured.", await OnFrameworkAsync(createSnapshot).ConfigureAwait(false));
            case "get-control-surface":
                return AgentBridgeResponse.Ok("Control surface captured.", await OnFrameworkAsync(createControlSurface).ConfigureAwait(false));
            case "get-review-surfaces":
                return AgentBridgeResponse.Ok("Review surfaces captured.", manifest.ReviewSurfaces);
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
                    var receipt = await setPluginEnabled(request.Target, enable, cancellationToken).ConfigureAwait(false);
                    return AgentBridgeResponse.Ok(enable ? "Plugin enabled." : "Plugin disabled.", receipt);
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or OperationCanceledException)
                {
                    return AgentBridgeResponse.Fail($"Plugin lifecycle change failed: {exception.Message}");
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

    private async Task<T> OnFrameworkAsync<T>(Func<T> action)
    {
        T? result = default;
        await dispatchOnFramework(() => result = action()).ConfigureAwait(false);
        return result!;
    }
}
