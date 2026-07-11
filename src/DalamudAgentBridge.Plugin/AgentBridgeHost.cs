using Franthropy.Dalamud.AgentBridge;
using System;
using System.IO;
using System.IO.Pipes;
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
    private readonly Action openWindow;
    private readonly Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private CancellationTokenSource? cancellation;
    private Task? listenTask;
    private string? accessToken;

    public AgentBridgeHost(Configuration configuration, string configDirectory, Func<Action, Task> dispatchOnFramework, Func<object> createSnapshot, Action openWindow, Func<bool, CancellationToken, Task<AgentBridgeCaptureReceipt>> captureViewport)
    {
        this.configuration = configuration;
        this.configDirectory = configDirectory;
        this.dispatchOnFramework = dispatchOnFramework;
        this.createSnapshot = createSnapshot;
        this.openWindow = openWindow;
        this.captureViewport = captureViewport;
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
            case "open-main-window":
                await dispatchOnFramework(openWindow).ConfigureAwait(false);
                return AgentBridgeResponse.Ok("Agent Bridge window opened.");
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
