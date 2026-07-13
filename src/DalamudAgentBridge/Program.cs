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
    "close-main-window",
    "open-acquisition-diagnostics",
    "select-main-tab",
    "capture-input-state",
    "stop-route",
    "capture-screen",
    "get-review-surfaces",
    "get-control-surface",
    "get-control",
    "invoke-control",
};

app.MapGet("/api/bridges", (BridgeRegistry registry) =>
    registry.Discover().Select(instance => new BridgeInstanceView(
        instance.Id,
        instance.PluginName,
        instance.PipeName,
        instance.ProcessId,
        instance.SchemaVersion,
        instance.PluginInstanceId)));

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
    NamedPipeBridgeClient client,
    ReviewVault reviewVault,
    CancellationToken cancellationToken) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });

    try
    {
        var response = await client.SendAsync(instance, "capture-screen", request, cancellationToken);
        if (!response.Success || response.Receipt is not { } receiptElement)
            return Results.BadRequest(response);

        var receipt = receiptElement.Deserialize<BridgeCaptureReceipt>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (receipt == null ||
            receipt.ProcessId != instance.ProcessId ||
            receipt.Width is < 1 or > 16384 ||
            receipt.Height is < 1 or > 16384 ||
            !string.Equals(receipt.FileName, $"{receipt.CaptureId}.bin", StringComparison.Ordinal) ||
            !TryResolveCapturePath(instance, receipt.CaptureId, out var capturePath) ||
            !File.Exists(capturePath))
            return Results.Problem("Bridge returned an invalid capture receipt.", statusCode: StatusCodes.Status502BadGateway);

        byte[] pngBytes;
        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(capturePath, cancellationToken);
            try
            {
                pngBytes = AgentBridgeDataProtection.UnprotectBytes(encryptedBytes, instance.PluginInstanceId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedBytes);
            }
        }
        catch (CryptographicException)
        {
            return Results.Problem("Bridge capture decryption failed.", statusCode: StatusCodes.Status502BadGateway);
        }
        finally
        {
            File.Delete(capturePath);
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(pngBytes));
        if (!string.Equals(actualSha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(pngBytes);
            return Results.Problem("Bridge capture hash verification failed.", statusCode: StatusCodes.Status502BadGateway);
        }

        try
        {
            var review = reviewVault.Store(receipt, pngBytes);
            return Results.Ok(new
            {
                success = true,
                message = response.Message,
                receipt,
                review,
                imageUrl = $"/api/reviews/{review.Id}.png",
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pngBytes);
        }
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

app.MapPost("/api/bridges/{id}/unfocused-review-capture-requests", (
    string id,
    BridgeRegistry registry,
    UnfocusedReviewCaptureQueue captureQueue) =>
{
    var instance = registry.Find(id);
    if (instance == null)
        return Results.NotFound(new { success = false, message = "Bridge instance was not found." });
    var target = instance.PluginName switch
    {
        "DalamudAgentBridge" => "bridge.main-window",
        "MarketMafioso" => "mmf.main-window",
        _ => null,
    };
    if (target == null)
        return Results.BadRequest(new { success = false, message = "This plugin has not adopted the capture-presentation transaction protocol." });

    var request = captureQueue.Queue(instance, target);
    return Results.Accepted($"/api/unfocused-review-capture-requests/{request.RequestId}", new { success = true, request });
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

static bool TryResolveCapturePath(BridgeInstance instance, string captureId, out string path)
{
    path = string.Empty;
    if (!Guid.TryParseExact(captureId, "N", out _))
        return false;

    var bridgeDirectory = Path.GetDirectoryName(instance.DiscoveryPath);
    if (string.IsNullOrWhiteSpace(bridgeDirectory))
        return false;

    var captureDirectory = Path.GetFullPath(Path.Combine(bridgeDirectory, "captures"));
    var candidate = Path.GetFullPath(Path.Combine(captureDirectory, $"{captureId}.bin"));
    if (!candidate.StartsWith(captureDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return false;

    path = candidate;
    return true;
}
