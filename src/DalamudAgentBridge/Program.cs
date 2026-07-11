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
builder.Services.AddSingleton<CaptureVault>();
builder.Services.AddSingleton<LocalDashboardSession>();

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
    "open-acquisition-diagnostics",
    "select-main-tab",
    "capture-input-state",
    "stop-route",
    "capture-screen",
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
    CaptureVault captureVault,
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

        var captureHandle = captureVault.Store(pngBytes);
        return Results.Ok(new
        {
            success = true,
            message = response.Message,
            receipt,
            imageUrl = $"/api/captures/{captureHandle}.png",
        });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge capture failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/captures/{captureHandle}.png", async (
    string captureHandle,
    HttpContext httpContext,
    CaptureVault captureVault) =>
{
    if (!captureVault.TryTake(captureHandle, out var pngBytes))
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
