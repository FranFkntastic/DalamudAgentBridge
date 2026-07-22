using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Travel;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudAgentBridge.Plugin;

public sealed class AgentBridgeHost : IDisposable
{
    private const int MaxRequestCharacters = 16_384;
    private readonly Configuration configuration;
    private readonly string configDirectory;
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
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? cancellation;
    private Task? listenTask;
    private string? accessToken;

    public AgentBridgeHost(Configuration configuration, string configDirectory, Func<Action, Task> dispatchOnFramework, Func<object> createSnapshot, Func<object> createControlSurface, Func<string, AgentBridgeUiControlReview> reviewControl, Func<string, long, AgentBridgeUiControlInvocation> invokeControl, Action openWindow, Func<IReadOnlyList<AgentBridgeCaptureSurfaceDescriptor>> getCaptureSurfaces, Func<string, AgentBridgeUiCaptureTransactionHandle> beginCapturePresentation, Func<string, AgentBridgeUiCaptureTransactionResult> completeCapturePresentation, Func<string, AgentBridgeUiCaptureTransactionResult> cancelCapturePresentation, Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport, Func<object> createPluginSnapshot, Func<string, bool, CancellationToken, Task<object>> setPluginEnabled, Func<object> createLoginSnapshot, Func<string, LifestreamLoginSubmissionResult> beginLogin)
    {
        this.configuration = configuration;
        this.configDirectory = configDirectory;
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
    }

    public string PipeName => $"DalamudAgentBridge.{Environment.ProcessId}";

    public void Start()
    {
        if (listenTask != null) return;
        accessToken = GetOrCreateAccessToken();
        Directory.CreateDirectory(BridgeDirectory);
        File.WriteAllText(DiscoveryPath, JsonSerializer.Serialize(new AgentBridgeDiscovery { SchemaVersion = 1, PipeName = PipeName, ProcessId = Environment.ProcessId, PluginInstanceId = configuration.PluginInstanceId }, jsonOptions));
        cancellation = new CancellationTokenSource();
        listenTask = Task.Run(() => ListenLoopAsync(cancellation.Token));
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var response = await HandleRequestAsync(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, jsonOptions)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
        }
    }

    private async Task<AgentBridgeResponse> HandleRequestAsync(string? requestJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestJson) || requestJson.Length > MaxRequestCharacters) return AgentBridgeResponse.Fail("Invalid bridge request.");
        AgentBridgeRequest? request;
        try { request = JsonSerializer.Deserialize<AgentBridgeRequest>(requestJson, jsonOptions); }
        catch (JsonException) { return AgentBridgeResponse.Fail("Bridge request JSON is invalid."); }
        if (request == null || !string.Equals(request.Token, accessToken, StringComparison.Ordinal)) return AgentBridgeResponse.Fail("Bridge authentication failed.");
        switch (request.Command?.Trim().ToLowerInvariant())
        {
            case "hello": return AgentBridgeResponse.Ok("Bridge is ready.");
            case "get-snapshot":
                object? snapshot = null;
                await dispatchOnFramework(() => snapshot = createSnapshot()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Snapshot captured.", snapshot);
            case "get-control-surface":
                object? controlSurface = null;
                await dispatchOnFramework(() => controlSurface = createControlSurface()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Control surface captured.", controlSurface);
            case "get-control":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A control ID is required.");
                AgentBridgeUiControlReview? controlReview = null;
                await dispatchOnFramework(() => controlReview = reviewControl(request.Target)).ConfigureAwait(false);
                return controlReview!.Control == null
                    ? new AgentBridgeResponse { Success = false, Message = "The requested control is not rendered.", Receipt = controlReview }
                    : AgentBridgeResponse.Ok("Reviewed control captured.", controlReview);
            case "invoke-control":
                if (string.IsNullOrWhiteSpace(request.Target) || request.FrameId is not { } frameId)
                    return AgentBridgeResponse.Fail("A control ID and rendered frame ID are required.");
                AgentBridgeUiControlInvocation? invocation = null;
                await dispatchOnFramework(() => invocation = invokeControl(request.Target, frameId)).ConfigureAwait(false);
                return invocation!.Success
                    ? AgentBridgeResponse.Ok(invocation.Message, invocation.Frame)
                    : new AgentBridgeResponse { Success = false, Message = invocation.Message, Receipt = invocation.Frame };
            case "open-main-window":
                await dispatchOnFramework(openWindow).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Agent Bridge window opened.");
            case "get-capture-surfaces":
                return AgentBridgeResponse.Ok("Capture surfaces captured.", getCaptureSurfaces());
            case "get-login-ui":
                object? loginSnapshot = null;
                await dispatchOnFramework(() => loginSnapshot = createLoginSnapshot()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Rendered title and login UI captured without requiring a local player.", loginSnapshot);
            case "begin-login":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A Character Name@Home World target is required.");
                LifestreamLoginSubmissionResult? loginReceipt = null;
                await dispatchOnFramework(() => loginReceipt = beginLogin(request.Target)).ConfigureAwait(false);
                return loginReceipt!.Success
                    ? AgentBridgeResponse.Ok("Login submitted; rendered and logged-in postconditions remain required.", loginReceipt)
                    : new AgentBridgeResponse { Success = false, Message = loginReceipt.Message, Receipt = loginReceipt };
            case "list-plugins":
                object? pluginSnapshot = null;
                await dispatchOnFramework(() => pluginSnapshot = createPluginSnapshot()).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Installed plugin state captured.", pluginSnapshot);
            case "enable-plugin":
            case "disable-plugin":
                if (string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("A plugin internal name is required.");
                try
                {
                    var enable = string.Equals(request.Command, "enable-plugin", StringComparison.OrdinalIgnoreCase);
                    var lifecycleReceipt = await setPluginEnabled(request.Target, enable, cancellationToken).ConfigureAwait(false);
                    return AgentBridgeResponse.Ok(enable ? "Plugin enabled." : "Plugin disabled.", lifecycleReceipt);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or OperationCanceledException)
                {
                    return AgentBridgeResponse.Fail($"Plugin lifecycle change failed: {ex.Message}");
                }
            case "begin-capture-presentation":
                if (!configuration.EnableScreenshots) return AgentBridgeResponse.Fail("Agent Bridge screenshots are disabled in the in-game plugin settings.");
                if (string.IsNullOrWhiteSpace(request.Target) ||
                    !getCaptureSurfaces().Any(surface => string.Equals(surface.Id, request.Target, StringComparison.Ordinal)))
                    return AgentBridgeResponse.Fail("The requested capture presentation target is not registered.");
                AgentBridgeUiCaptureTransactionHandle? handle = null;
                try
                {
                    await dispatchOnFramework(() => handle = beginCapturePresentation(request.Target!)).ConfigureAwait(false);
                    var ready = await handle!.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return AgentBridgeResponse.Ok("Capture presentation rendered and ready.", ready);
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
                {
                    if (handle != null)
                        await dispatchOnFramework(() => cancelCapturePresentation(handle.TransactionId)).ConfigureAwait(false);
                    return AgentBridgeResponse.Fail($"Capture presentation failed: {ex.Message}");
                }
            case "complete-capture-presentation":
            case "cancel-capture-presentation":
                if (string.IsNullOrWhiteSpace(request.TransactionId)) return AgentBridgeResponse.Fail("A capture transaction identifier is required.");
                AgentBridgeUiCaptureTransactionResult? result = null;
                await dispatchOnFramework(() => result = string.Equals(request.Command, "complete-capture-presentation", StringComparison.OrdinalIgnoreCase)
                    ? completeCapturePresentation(request.TransactionId)
                    : cancelCapturePresentation(request.TransactionId)).ConfigureAwait(false);
                return result!.Success ? AgentBridgeResponse.Ok(result.Message, result) : AgentBridgeResponse.Fail(result.Message);
            case "capture-screen":
                if (!configuration.EnableScreenshots) return AgentBridgeResponse.Fail("Agent Bridge screenshots are disabled in the in-game plugin settings.");
                if (!string.IsNullOrWhiteSpace(request.Target)) return AgentBridgeResponse.Fail("This independent bridge has no plugin-specific target surfaces.");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    try { return AgentBridgeResponse.Ok("Rendered viewport captured.", await captureViewport(request.FullViewport, timeout.Token).ConfigureAwait(false)); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return AgentBridgeResponse.Fail("Rendered viewport capture timed out."); }
                    catch (Exception ex) { return AgentBridgeResponse.Fail($"Rendered viewport capture failed: {ex.Message}"); }
                }
            default: return AgentBridgeResponse.Fail("Bridge command is not allowed by this independent host.");
        }
    }

    public void Dispose()
    {
        var active = Interlocked.Exchange(ref cancellation, null);
        if (active != null) { active.Cancel(); active.Dispose(); }
        listenTask = null;
        accessToken = null;
        if (File.Exists(DiscoveryPath)) File.Delete(DiscoveryPath);
    }

    private string GetOrCreateAccessToken()
    {
        if (!string.IsNullOrWhiteSpace(configuration.AgentBridgeProtectedAccessToken))
        {
            try { return AgentBridgeDataProtection.UnprotectToken(configuration.AgentBridgeProtectedAccessToken, configuration.PluginInstanceId); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { configuration.AgentBridgeProtectedAccessToken = string.Empty; }
        }
        var token = Guid.NewGuid().ToString("N");
        configuration.AgentBridgeProtectedAccessToken = AgentBridgeDataProtection.ProtectToken(token, configuration.PluginInstanceId);
        configuration.Save();
        return token;
    }

    private string BridgeDirectory => Path.Combine(configDirectory, "agent-bridge");
    private string DiscoveryPath => Path.Combine(BridgeDirectory, $"discovery-{Environment.ProcessId}.json");
}
