using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DalamudAgentBridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DalamudAgentBridge.Mcp;

[McpServerToolType]
public sealed class AgentBridgeTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly AgentBridgeClient client;
    private readonly DevPluginDeploymentService deployment;
    private readonly PluginCaptureService capture;
    private readonly ReviewVault reviewVault;

    public AgentBridgeTools(
        AgentBridgeClient client,
        DevPluginDeploymentService deployment,
        PluginCaptureService capture,
        ReviewVault reviewVault)
    {
        this.client = client;
        this.deployment = deployment;
        this.capture = capture;
        this.reviewVault = reviewVault;
    }

    [McpServerTool(Name = "bridge_list"), Description("List live authenticated Dalamud plugin bridges and their stable profile identities. Read-only.")]
    public string List() => Json(client.List());

    [McpServerTool(Name = "bridge_health"), Description("Check that a plugin bridge is reachable and return its exact loaded assembly identity and capability manifest. Read-only.")]
    public async Task<string> Health(
        [Description("Plugin internal name, for example RQ or MarketMafioso.")] string plugin,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id when a profile has multiple clients.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.GetHealthAsync(Target(plugin, profile, processId), cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_manifest"), Description("Read the selected plugin bridge's versioned capabilities, semantic actions, review surfaces, and exact runtime identity. Read-only.")]
    public async Task<string> Manifest(
        [Description("Plugin internal name.")] string plugin,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.GetManifestAsync(Target(plugin, profile, processId), cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_snapshot"), Description("Read the plugin's current automation and UI state snapshot. Read-only and safe while the game is unfocused.")]
    public async Task<string> Snapshot(
        [Description("Plugin internal name.")] string plugin,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.GetSnapshotAsync(Target(plugin, profile, processId), cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_wait"), Description("Wait until a dot-path in the plugin snapshot exists or equals a value. This subscribes the caller to an observable completion condition instead of guessing with sleeps.")]
    public async Task<string> Wait(
        [Description("Plugin internal name.")] string plugin,
        [Description("Dot-separated snapshot path, for example refreshActive.")] string path,
        [Description("Optional expected scalar value. Omit to wait only for the path to exist.")] string? equals = null,
        [Description("Maximum wait in milliseconds, from 250 through 300000.")] int timeoutMilliseconds = 30000,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.WaitForSnapshotAsync(
            Target(plugin, profile, processId),
            new BridgeWaitCondition(path, equals),
            TimeSpan.FromMilliseconds(Math.Clamp(timeoutMilliseconds, 250, 300000)),
            cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_logs"), Description("Read action-scoped Dalamud log lines for the selected client. Use the returned nextCursor on the next call. Read-only.")]
    public string Logs(
        [Description("Plugin internal name.")] string plugin,
        [Description("Exclusive cursor returned by a previous call. Omit for the newest log window.")] long? cursor = null,
        [Description("Maximum number of lines, capped by the bridge.")] int? limit = null,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null) =>
        Json(client.ReadLogs(Target(plugin, profile, processId), cursor, limit));

    [McpServerTool(Name = "bridge_act"), Description("Invoke one manifest-declared semantic control after the bridge renders and reviews that exact control. This cannot inject arbitrary mouse or keyboard input and cannot bypass the plugin allowlist.")]
    public async Task<string> Act(
        [Description("Plugin internal name.")] string plugin,
        [Description("Manifest review surface id.")] string surfaceId,
        [Description("Manifest control id to invoke.")] string controlId,
        [Description("Optional JSON object containing typed arguments declared by the action schema.")] string? argumentsJson = null,
        [Description("Wait for the returned operation or completion condition.")] bool waitForCompletion = true,
        [Description("Maximum completion wait in milliseconds.")] int timeoutMilliseconds = 30000,
        [Description("Optional snapshot dot-path used when the action does not return an operation id.")] string? waitPath = null,
        [Description("Expected value for waitPath.")] string? waitEquals = null,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        JsonElement? arguments = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("argumentsJson must be a JSON object.", nameof(argumentsJson));
            arguments = document.RootElement.Clone();
        }
        return Json(await client.ActAndObserveAsync(
            Target(plugin, profile, processId),
            new ReviewedControlActionRequest
            {
                SurfaceId = surfaceId,
                ControlId = controlId,
                Arguments = arguments,
                WaitForCompletion = waitForCompletion,
                CompletionTimeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 250, 300000),
                CompletionCondition = string.IsNullOrWhiteSpace(waitPath) ? null : new BridgeWaitCondition(waitPath, waitEquals),
            },
            cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "bridge_deploy"), Description("Deploy a built dev-plugin directory to the exact directory of the selected loaded plugin, wait for hot reload, and prove the loaded DLL SHA-256. Installed package directories are refused and failed deployments roll back.")]
    public async Task<string> Deploy(
        [Description("Plugin internal name.")] string plugin,
        [Description("Absolute directory containing the built plugin DLL and manifest JSON.")] string sourceDirectory,
        [Description("Optional expected source DLL SHA-256.")] string? expectedSha256 = null,
        [Description("Maximum hot-reload proof wait in milliseconds.")] int timeoutMilliseconds = 20000,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await deployment.DeployAsync(
            Target(plugin, profile, processId),
            new DevPluginDeploymentRequest
            {
                SourceDirectory = sourceDirectory,
                ExpectedMainDllSha256 = expectedSha256,
                TimeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 1000, 120000),
            },
            cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_capture"), Description("Capture a plugin-declared review surface through its authenticated bridge, verify the encrypted handoff and SHA-256, and return the PNG directly. No desktop-control or arbitrary screen-capture permission is granted.")]
    public async Task<CallToolResult> Capture(
        [Description("Plugin internal name.")] string plugin,
        [Description("Optional manifest capture target. Omit for the plugin's default review surface.")] string? target = null,
        [Description("Capture the complete declared viewport rather than its default review region.")] bool fullViewport = false,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        var receipt = await capture.CaptureAsync(
            Target(plugin, profile, processId),
            new BridgeCommandRequest { Target = target, FullViewport = fullViewport },
            cancellationToken).ConfigureAwait(false);
        if (!reviewVault.TryRead(receipt.Review.Id, out var pngBytes))
            throw new InvalidOperationException("The verified review image could not be read from the short-lived vault.");
        try
        {
            var base64Bytes = Encoding.UTF8.GetBytes(Convert.ToBase64String(pngBytes));
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = Json(receipt) },
                    new ImageContentBlock { Data = base64Bytes, MimeType = "image/png" },
                ],
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pngBytes);
        }
    }

    private static BridgeTargetSelector Target(string plugin, string profile, int? processId) =>
        new(plugin, profile, processId);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
