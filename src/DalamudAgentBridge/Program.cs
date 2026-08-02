using DalamudAgentBridge;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.AgentBridge;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.UseUrls(builder.Configuration["Bridge:Url"] ?? "http://127.0.0.1:45831");
builder.Services.AddSingleton<BridgeRegistry>();
builder.Services.AddSingleton<NamedPipeBridgeClient>();
builder.Services.AddSingleton<ReviewVault>();
builder.Services.AddSingleton<LocalDashboardSession>();
builder.Services.AddSingleton<CompositedGameWindowCaptureService>();
builder.Services.AddSingleton<CompositedCaptureQueue>();
builder.Services.AddSingleton<WindowsGraphicsCaptureService>();
builder.Services.AddSingleton<UnfocusedReviewCaptureQueue>();
builder.Services.AddSingleton<DalamudLogWatcher>();
builder.Services.AddSingleton<ReviewedControlPresentationService>();
builder.Services.AddSingleton<CaptureSurfaceDiscoveryService>();
builder.Services.AddSingleton<PluginLifecycleClient>();
builder.Services.AddSingleton<IPluginLifecycleClient>(services => services.GetRequiredService<PluginLifecycleClient>());
builder.Services.AddSingleton<LocalPluginBuildReplacementService>();
builder.Services.AddSingleton<AgentBridgeClient>();
builder.Services.AddSingleton<DevPluginDeploymentService>();
builder.Services.AddSingleton<PluginCaptureService>();
builder.Services.AddSingleton<DiagnosticClipService>();
builder.Services.AddSingleton<PluginSurfaceCaptureService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store, no-cache, private";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; connect-src 'self'; img-src 'self' blob:; style-src 'self'; script-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    if (context.Request.Path.StartsWithSegments("/repository"))
    {
        await next();
        return;
    }

    var session = context.RequestServices.GetRequiredService<LocalDashboardSession>();
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !string.Equals(origin, "http://127.0.0.1:45831", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!session.IsAuthenticated(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    else if (HttpMethods.IsGet(context.Request.Method))
    {
        session.Establish(context);
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

var allowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "hello",
    "get-snapshot",
    "get-proof",
    "capture-proof",
    "open-main-window",
    "present-surface",
    "close-main-window",
    "open-acquisition-diagnostics",
    "begin-login",
    "get-login-ui",
    "get-character-provisioning",
    "select-main-tab",
    "capture-input-state",
    "stop-route",
    "capture-screen",
    "get-capture-surfaces",
    "get-review-surfaces",
    "get-control-surface",
    "get-control",
    "invoke-control",
    "list-plugins",
    "get-plugin-surfaces",
    "begin-plugin-surface-presentation",
    "restore-plugin-surface-presentation",
    "capture-plugin-surface",
    "enable-plugin",
    "disable-plugin",
    "install-plugin",
    "install-dev-plugin",
    "get-situation",
    "get-navigation",
    "navigate-to",
    "cancel-navigation",
    "get-specialists",
    "start-specialist",
    "cancel-specialist",
};

app.MapGet("/api/bridges", (AgentBridgeClient client) => client.List());

app.MapGet("/api/bridges/{id}/situation", async (
    string id,
    BridgeRegistry registry,
    NamedPipeBridgeClient pipe,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var response = await pipe.SendAsync(instance, "get-situation", null, cancellationToken).ConfigureAwait(false);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Situation read failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/specialists", async (
    string id,
    BridgeRegistry registry,
    NamedPipeBridgeClient pipe,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var response = await pipe.SendAsync(instance, "get-specialists", null, cancellationToken).ConfigureAwait(false);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Specialist catalog read failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/specialists/{capabilityId}", async (
    string id,
    string capabilityId,
    SpecialistOperationHttpRequest request,
    BridgeRegistry registry,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var response = await client.StartSpecialistAsync(
            instance,
            new SpecialistStartRequest(capabilityId, request.Parameters, request.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Specialist start failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/specialists/cancel", async (
    string id,
    string? operationId,
    BridgeRegistry registry,
    NamedPipeBridgeClient pipe,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var response = await pipe.SendAsync(
            instance,
            "cancel-specialist",
            new BridgeCommandRequest { OperationId = operationId },
            cancellationToken).ConfigureAwait(false);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Specialist cancellation failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/diagnostic-clips", async (
    string id,
    DiagnosticClipRequest request,
    BridgeRegistry registry,
    DiagnosticClipService clips,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var receipt = await clips.CaptureAsync(instance, request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { success = receipt.Failure is null, message = receipt.Failure ?? "Diagnostic clip captured.", receipt });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Diagnostic clip failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/plugin-surfaces", async (
    string? plugin,
    string? profile,
    int? processId,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await client.GetPluginSurfaceCatalogAsync(
            plugin,
            string.IsNullOrWhiteSpace(profile) ? "primary" : profile,
            processId,
            cancellationToken).ConfigureAwait(false));
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem($"Plugin surface discovery failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/plugin-surfaces/{surfaceId}/presentations", async (
    string surfaceId,
    string plugin,
    string? profile,
    int? processId,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await client.BeginPluginSurfacePresentationAsync(
            plugin, surfaceId, profile ?? "primary", processId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { success = true, message = "Surface presented under a short-lived reversible lease.", receipt });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem($"Plugin surface presentation failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapDelete("/api/plugin-surface-presentations/{transactionId}", async (
    string transactionId,
    string? profile,
    int? processId,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var result = await client.RestorePluginSurfacePresentationAsync(
        transactionId, profile ?? "primary", processId, cancellationToken).ConfigureAwait(false);
    return result.Success ? Results.Ok(result) : Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict);
});

app.MapPost("/api/plugin-surfaces/{surfaceId}/captures", async (
    string surfaceId,
    string plugin,
    string? profile,
    int? processId,
    PluginSurfaceCaptureService capture,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await capture.CaptureAsync(
            plugin, surfaceId, profile ?? "primary", processId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            success = true,
            message = "Surface presented, captured, and restored.",
            receipt,
            imageUrl = receipt.Capture.ImagePath,
        });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem($"Plugin surface capture failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/targets/{profile}/{plugin}/health", async (
    string profile,
    string plugin,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await client.GetHealthAsync(new BridgeTargetSelector(plugin, profile), cancellationToken);
        return receipt.Reachable ? Results.Ok(new { success = true, receipt }) : Results.Problem(receipt.Message, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
    {
        return Results.NotFound(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/targets/{profile}/{plugin}/snapshot", async (
    string profile,
    string plugin,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await client.GetSnapshotAsync(new BridgeTargetSelector(plugin, profile), cancellationToken);
        return Results.Ok(new { success = true, message = "Snapshot captured.", receipt });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/targets/{profile}/{plugin}/logs", (
    string profile,
    string plugin,
    long? cursor,
    int? limit,
    AgentBridgeClient client) =>
{
    try
    {
        return Results.Ok(new { success = true, receipt = client.ReadLogs(new BridgeTargetSelector(plugin, profile), cursor, limit) });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/targets/{profile}/{plugin}/chat", async (
    string profile,
    string plugin,
    long? cursor,
    int? limit,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await client.ReadChatLogAsync(new BridgeTargetSelector(plugin, profile), cursor, limit, cancellationToken);
        return Results.Ok(new { success = true, receipt });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/targets/{profile}/{plugin}/wait", async (
    string profile,
    string plugin,
    BridgeWaitRequest request,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMilliseconds ?? 30_000, 250, 300_000));
        var receipt = await client.WaitForSnapshotAsync(
            new BridgeTargetSelector(plugin, profile), request.Condition, timeout, cancellationToken);
        return Results.Ok(new { success = true, message = "Snapshot condition satisfied.", receipt });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Snapshot condition was not satisfied before the timeout.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/targets/{profile}/{plugin}/actions", async (
    string profile,
    string plugin,
    ReviewedControlActionRequest request,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await client.ActAndObserveAsync(new BridgeTargetSelector(plugin, profile), request, cancellationToken);
        return Results.Ok(new { success = true, message = "Reviewed action completed with its observation receipt.", receipt });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Reviewed action did not satisfy its completion contract before the timeout.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/targets/{profile}/{plugin}/deploy", async (
    string profile,
    string plugin,
    DevPluginDeploymentRequest request,
    DevPluginDeploymentService deployment,
    CancellationToken cancellationToken) =>
{
    try
    {
        var receipt = await deployment.DeployAsync(new BridgeTargetSelector(plugin, profile), request, cancellationToken);
        return Results.Ok(new { success = true, message = "Dev plugin deployed and exact loaded identity verified.", receipt });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Dev-plugin reload verification timed out.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or KeyNotFoundException or TimeoutException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/events", async (HttpContext context, AgentBridgeClient client, CancellationToken cancellationToken) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    string? previous = null;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var current = JsonSerializer.Serialize(client.List());
            if (!string.Equals(previous, current, StringComparison.Ordinal))
            {
                await context.Response.WriteAsync($"event: bridges\ndata: {current}\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                previous = current;
            }
            await Task.Delay(250, cancellationToken);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
});

app.MapGet("/api/bridges/{id}/snapshot", async (
    string id,
    BridgeRegistry registry,
    NamedPipeBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        return Results.Ok(await client.SendAsync(instance, "get-snapshot", null, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge snapshot failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/manifest", async (
    string id,
    BridgeRegistry registry,
    AgentBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        return Results.Ok(new { success = true, message = "Bridge manifest captured.", receipt = await client.GetManifestAsync(instance, cancellationToken) });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or InvalidDataException)
    {
        return Results.Problem($"Bridge manifest failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/plugins", async (
    string id,
    BridgeRegistry registry,
    PluginLifecycleClient lifecycleClient,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var receipt = await lifecycleClient.ListAsync(instance, cancellationToken);
        return Results.Ok(new { success = true, message = "Installed plugin state captured.", receipt });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
    {
        return Results.Problem($"Plugin state read failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/plugins/{internalName}/enable", async (
    string id,
    string internalName,
    BridgeRegistry registry,
    PluginLifecycleClient lifecycleClient,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        return Results.Ok(await lifecycleClient.SetEnabledAsync(instance, internalName, true, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
    {
        return Results.Problem($"Plugin enable failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/plugins/{internalName}/disable", async (
    string id,
    string internalName,
    BridgeRegistry registry,
    PluginLifecycleClient lifecycleClient,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        return Results.Ok(await lifecycleClient.SetEnabledAsync(instance, internalName, false, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
    {
        return Results.Problem($"Plugin disable failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/plugins/{internalName}/install", async (
    string id,
    string internalName,
    BridgeRegistry registry,
    PluginLifecycleClient lifecycleClient,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        return Results.Ok(await lifecycleClient.InstallAsync(instance, internalName, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.Problem($"Plugin install failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/plugins/{internalName}/install-dev", async (
    string id,
    string internalName,
    BridgeRegistry registry,
    PluginLifecycleClient lifecycleClient,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        return Results.Ok(await lifecycleClient.InstallDevAsync(instance, internalName, cancellationToken));
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.Problem($"Dev plugin install failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/plugins/{internalName}/local-build", async (
    string id,
    string internalName,
    LocalPluginBuildReplacementRequest request,
    BridgeRegistry registry,
    LocalPluginBuildReplacementService replacementService,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var receipt = await replacementService.ReplaceAsync(instance, internalName, request, cancellationToken);
        return Results.Ok(new { success = true, message = "Local plugin build installed and verified.", receipt });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or KeyNotFoundException or OperationCanceledException or AggregateException)
    {
        return Results.Problem($"Local plugin build replacement failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/logs", (
    string id,
    long? cursor,
    int? limit,
    BridgeRegistry registry,
    DalamudLogWatcher watcher) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        return Results.Ok(new { success = true, message = "Dalamud log entries captured.", receipt = watcher.Read(instance, cursor, limit) });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return Results.Problem($"Dalamud log read failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/controls", async (
    string id,
    BridgeRegistry registry,
    NamedPipeBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var response = await client.SendAsync(instance, "get-control-surface", null, cancellationToken);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge control-surface read failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/controls/{controlId}", async (
    string id,
    string controlId,
    BridgeRegistry registry,
    NamedPipeBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var response = await client.SendAsync(instance, "get-control", new BridgeCommandRequest { Target = controlId }, cancellationToken);
        return response.Success ? Results.Ok(response) : Results.NotFound(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge control review failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/control-presentations", async (
    string id,
    ReviewedControlPresentationRequest request,
    BridgeRegistry registry,
    ReviewedControlPresentationService presentationService,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var receipt = await presentationService.PresentAsync(instance, request, cancellationToken);
        return Results.Ok(new { success = true, message = "Reviewed controls are presented and ready.", receipt });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Reviewed controls did not become ready before the presentation timeout.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
    {
        return Results.Problem($"Reviewed control presentation failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/control-actions", async (
    string id,
    ReviewedControlActionRequest request,
    BridgeRegistry registry,
    ReviewedControlPresentationService presentationService,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    try
    {
        var receipt = await presentationService.PresentAndInvokeAsync(instance, request, cancellationToken);
        return Results.Ok(new { success = true, message = "Reviewed control was presented and invoked.", receipt });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Reviewed control did not become ready before the action timeout.", statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
    {
        return Results.Problem($"Reviewed control action failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/review-surfaces", async (
    string id,
    BridgeRegistry registry,
    NamedPipeBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var response = await client.SendAsync(instance, "get-review-surfaces", null, cancellationToken);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge review-surface discovery failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/capture-surfaces", async (
    string id,
    BridgeRegistry registry,
    CaptureSurfaceDiscoveryService discoveryService,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var receipt = await discoveryService.GetAsync(instance, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { success = true, message = "Capture surfaces discovered.", receipt });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
    {
        return Results.Problem($"Bridge capture-surface discovery failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/controls/{controlId}/invoke", async (
    string id,
    string controlId,
    BridgeCommandRequest? request,
    BridgeRegistry registry,
    NamedPipeBridgeClient client,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var response = await client.SendAsync(instance, "invoke-control", new BridgeCommandRequest
        {
            Target = controlId,
            FrameId = request?.FrameId,
        }, cancellationToken);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge control action failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/commands/{command}", async (
    string id,
    string command,
    BridgeCommandRequest? request,
    BridgeRegistry registry,
    NamedPipeBridgeClient client,
    CancellationToken cancellationToken) =>
{
    if (!allowedCommands.Contains(command))
        return Results.BadRequest(new { success = false, message = "Command is not allowlisted by the control utility." });

    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var response = await client.SendAsync(instance, command, request, cancellationToken);
        return response.Success ? Results.Ok(response) : Results.BadRequest(response);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge command failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/captures", async (
    string id,
    HttpContext httpContext,
    BridgeCommandRequest? request,
    BridgeRegistry registry,
    PluginCaptureService captureService,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var capture = await captureService.CaptureAsync(instance, request, cancellationToken);
        return Results.Ok(new
        {
            success = true,
            message = "Rendered viewport captured and verified.",
            receipt = capture.Receipt,
            review = capture.Review,
            imageUrl = capture.ImagePath,
        });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge capture failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/bridges/{id}/composited-captures", (
    string id,
    BridgeRegistry registry,
    CompositedGameWindowCaptureService captureService,
    ReviewVault reviewVault) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    byte[]? pngBytes = null;
    try
    {
        var capture = captureService.Capture(instance.ProcessId);
        pngBytes = capture.PngBytes;
        var receipt = new BridgeCaptureReceipt
        {
            SchemaVersion = 1,
            CaptureId = Guid.NewGuid().ToString("N"),
            FileName = "composited-window-memory",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Width = capture.Width,
            Height = capture.Height,
            Sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)),
            ProcessId = instance.ProcessId,
            Scope = "CompositedGameWindow",
        };
        var review = reviewVault.Store(receipt, pngBytes);
        return Results.Ok(new
        {
            success = true,
            message = "Foreground FFXIV client area captured from the final compositor output.",
            receipt,
            review,
            imageUrl = $"/api/reviews/{review.Id}.png",
        });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return Results.BadRequest(new { success = false, message = $"Composited capture failed: {ex.Message}" });
    }
    finally
    {
        if (pngBytes != null)
            CryptographicOperations.ZeroMemory(pngBytes);
    }
});

app.MapPost("/api/bridges/{id}/wgc-captures", async (
    string id,
    BridgeRegistry registry,
    WindowsGraphicsCaptureService captureService,
    ReviewVault reviewVault,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    byte[]? pngBytes = null;
    try
    {
        var capture = await captureService.CaptureAsync(instance.ProcessId, cancellationToken);
        pngBytes = capture.PngBytes;
        var receipt = new BridgeCaptureReceipt
        {
            SchemaVersion = 1,
            CaptureId = Guid.NewGuid().ToString("N"),
            FileName = "windows-graphics-capture-memory",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Width = capture.Width,
            Height = capture.Height,
            Sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)),
            ProcessId = instance.ProcessId,
            Scope = "WindowsGraphicsCaptureMainWindow",
        };
        var review = reviewVault.Store(receipt, pngBytes);
        return Results.Ok(new
        {
            success = true,
            message = "FFXIV main window captured without changing the foreground application.",
            receipt,
            review,
            imageUrl = $"/api/reviews/{review.Id}.png",
        });
    }
    catch (WindowsGraphicsCaptureException ex)
    {
        return Results.BadRequest(new
        {
            success = false,
            failure = ex.Failure.ToString(),
            message = $"Windows Graphics Capture failed: {ex.Message}",
        });
    }
    finally
    {
        if (pngBytes != null)
            CryptographicOperations.ZeroMemory(pngBytes);
    }
});

app.MapPost("/api/bridges/{id}/composited-capture-requests", (
    string id,
    BridgeRegistry registry,
    CompositedCaptureQueue captureQueue) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    var request = captureQueue.Queue(instance.ProcessId);
    return Results.Accepted($"/api/composited-capture-requests/{request.RequestId}", new { success = true, request });
});

app.MapGet("/api/composited-capture-requests/{requestId}", (string requestId, CompositedCaptureQueue captureQueue) =>
    captureQueue.TryGet(requestId, out var request)
        ? Results.Ok(new { success = true, request })
        : Results.NotFound(new { success = false, message = "Composited capture request was not found or has expired." }));

app.MapPost("/api/bridges/{id}/unfocused-review-capture-requests", async (
    string id,
    string? target,
    BridgeRegistry registry,
    CaptureSurfaceDiscoveryService discoveryService,
    UnfocusedReviewCaptureQueue captureQueue,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var surface = await discoveryService.ResolveAsync(instance, target, cancellationToken).ConfigureAwait(false);
        var request = captureQueue.Queue(instance, surface.Id);
        return Results.Accepted($"/api/unfocused-review-capture-requests/{request.RequestId}", new { success = true, request });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
    {
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

app.MapGet("/api/unfocused-review-capture-requests/{requestId}", (string requestId, UnfocusedReviewCaptureQueue captureQueue) =>
    captureQueue.TryGet(requestId, out var request)
        ? Results.Ok(new { success = true, request })
        : Results.NotFound(new { success = false, message = "Unfocused review capture request was not found or has expired." }));

app.MapGet("/api/reviews", (ReviewVault reviewVault) =>
    Results.Ok(reviewVault.List()));

app.MapGet("/api/reviews/{reviewId}.png", async (
    string reviewId,
    HttpContext httpContext,
    ReviewVault reviewVault) =>
{
    if (!reviewVault.TryRead(reviewId, out var pngBytes))
    {
        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    try
    {
        httpContext.Response.ContentType = "image/png";
        httpContext.Response.ContentLength = pngBytes.Length;
        await httpContext.Response.Body.WriteAsync(pngBytes);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(pngBytes);
    }
});

app.MapDelete("/api/reviews/{reviewId}", (string reviewId, ReviewVault reviewVault) =>
    reviewVault.Delete(reviewId) ? Results.NoContent() : Results.NotFound());

app.MapFallbackToFile("index.html");
app.Run();
