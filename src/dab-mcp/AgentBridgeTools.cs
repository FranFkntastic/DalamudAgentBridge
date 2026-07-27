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
    private readonly PluginLifecycleClient lifecycle;
    private readonly DevPluginDeploymentService deployment;
    private readonly PluginCaptureService capture;
    private readonly PluginSurfaceCaptureService surfaceCapture;
    private readonly ReviewVault reviewVault;

    public AgentBridgeTools(
        AgentBridgeClient client,
        PluginLifecycleClient lifecycle,
        DevPluginDeploymentService deployment,
        PluginCaptureService capture,
        PluginSurfaceCaptureService surfaceCapture,
        ReviewVault reviewVault)
    {
        this.client = client;
        this.lifecycle = lifecycle;
        this.deployment = deployment;
        this.capture = capture;
        this.surfaceCapture = surfaceCapture;
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

    [McpServerTool(Name = "bridge_plugins"), Description("List installed Dalamud plugins and their public or safely discovered UI entry points through the standalone connector. Read-only.")]
    public async Task<string> Plugins(
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id when a profile has multiple clients.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.GetPluginSurfaceCatalogAsync(null, profile, processId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_surfaces"), Description("List public and bounded-reflection UI surfaces for one installed plugin. This only observes serialized state; it does not open, close, focus, or invoke the plugin. Read-only.")]
    public async Task<string> Surfaces(
        [Description("Installed plugin internal name.")] string plugin,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.GetPluginSurfaceCatalogAsync(plugin, profile, processId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_surface_inspect"), Description("Refresh and inspect one discovered plugin UI surface by stable surface id. Read-only; reflected objects never leave the connector.")]
    public async Task<string> InspectSurface(
        [Description("Installed plugin internal name.")] string plugin,
        [Description("Stable surface id returned by bridge_surfaces.")] string surfaceId,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        var catalog = await client.GetPluginSurfaceCatalogAsync(plugin, profile, processId, cancellationToken).ConfigureAwait(false);
        var surface = catalog.Plugins.SelectMany(value => value.Surfaces)
            .SingleOrDefault(value => string.Equals(value.Id, surfaceId, StringComparison.Ordinal));
        return surface is null
            ? Json(new { success = false, message = $"Surface {surfaceId} is not present in the current runtime catalog.", catalog.CatalogRevision })
            : Json(new { success = true, catalog.CapturedAtUtc, catalog.CatalogRevision, surface });
    }

    [McpServerTool(Name = "bridge_surface_present"), Description("Open and uncollapse one reflected plugin window under a short-lived reversible lease. Returns the exact prior state and transaction id; the connector auto-restores on expiry.")]
    public async Task<string> PresentSurface(
        [Description("Installed plugin internal name.")] string plugin,
        [Description("Reversible reflected surface id returned by bridge_surfaces.")] string surfaceId,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.BeginPluginSurfacePresentationAsync(
            plugin, surfaceId, profile, processId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_surface_restore"), Description("Finish a reflected surface presentation lease and restore the exact prior open, collapsed, and focus-request state.")]
    public async Task<string> RestoreSurface(
        [Description("Transaction id returned by bridge_surface_present.")] string transactionId,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.RestorePluginSurfacePresentationAsync(
            transactionId, profile, processId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_surface_capture"), Description("Present one reflected plugin window, capture the rendered game viewport, verify and store the encrypted handoff, then restore prior window state in a finally path.")]
    public async Task<CallToolResult> CaptureSurface(
        [Description("Installed plugin internal name.")] string plugin,
        [Description("Reversible reflected surface id returned by bridge_surfaces.")] string surfaceId,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        var receipt = await surfaceCapture.CaptureAsync(
            plugin, surfaceId, profile, processId, cancellationToken).ConfigureAwait(false);
        if (!reviewVault.TryRead(receipt.Capture.Review.Id, out var pngBytes))
            throw new InvalidOperationException("The verified surface review image could not be read from the short-lived vault.");
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

    [McpServerTool(Name = "bridge_chat_log"), Description("Read in-game chat log lines observed by the connector since it loaded, including other plugins' chat output. Use the returned nextCursor on the next call. Read-only.")]
    public async Task<string> ChatLog(
        [Description("Plugin internal name.")] string plugin,
        [Description("Exclusive cursor returned by a previous call. Omit for the newest chat window.")] long? cursor = null,
        [Description("Maximum number of lines, capped by the bridge.")] int? limit = null,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default) =>
        Json(await client.ReadChatLogAsync(Target(plugin, profile, processId), cursor, limit, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "bridge_act"), Description("Invoke one manifest-declared semantic control after the bridge renders and reviews that exact control. This cannot inject arbitrary mouse or keyboard input and cannot bypass the plugin allowlist.")]
    public async Task<string> Act(
        [Description("Plugin internal name.")] string plugin,
        [Description("Manifest review surface id. Omit when the action id uniquely identifies its surface.")] string? surfaceId = null,
        [Description("Manifest action id to invoke.")] string controlId = "",
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
        try
        {
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Json(new { success = false, message = "The reviewed control action timed out." });
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            return Json(new { success = false, message = exception.Message });
        }
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

    [McpServerTool(Name = "bridge_install_plugin"), Description("Install and load a plugin from the profile's configured Dalamud plugin repositories through the in-game connector. Refuses plugins that are already installed and cannot install over the bridge itself. Installs the release channel build.")]
    public async Task<string> InstallPlugin(
        [Description("Internal name of the plugin to install, for example MarketBoardPlugin.")] string plugin,
        [Description("XIVLauncher profile alias or stable profile id. Defaults to primary.")] string profile = "primary",
        [Description("Optional FFXIV process id.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var instance = client.Resolve(Target("DalamudAgentBridge", profile, processId));
            return Json(await lifecycle.InstallAsync(instance, plugin, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            return Json(new { success = false, message = exception.Message });
        }
    }

    private static BridgeTargetSelector Target(string plugin, string profile, int? processId) =>
        new(plugin, profile, processId);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
