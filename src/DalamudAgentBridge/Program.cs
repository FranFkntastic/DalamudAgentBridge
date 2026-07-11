using DalamudAgentBridge;
using System.Text.Json;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.UseUrls(builder.Configuration["Bridge:Url"] ?? "http://127.0.0.1:45831");
builder.Services.AddSingleton<BridgeRegistry>();
builder.Services.AddSingleton<NamedPipeBridgeClient>();

var app = builder.Build();
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
            !string.Equals(receipt.FileName, $"{receipt.CaptureId}.png", StringComparison.Ordinal) ||
            !TryResolveCapturePath(instance, receipt.CaptureId, out var capturePath) ||
            !File.Exists(capturePath))
            return Results.Problem("Bridge returned an invalid capture receipt.", statusCode: StatusCodes.Status502BadGateway);

        await using var captureStream = File.OpenRead(capturePath);
        var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(captureStream, cancellationToken));
        if (!string.Equals(actualSha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            return Results.Problem("Bridge capture hash verification failed.", statusCode: StatusCodes.Status502BadGateway);

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            success = true,
            message = response.Message,
            receipt,
            imageUrl = $"/api/bridges/{Uri.EscapeDataString(id)}/captures/{receipt.CaptureId}.png",
        });
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem($"Bridge capture failed: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/bridges/{id}/captures/{captureId}.png", (
    string id,
    string captureId,
    HttpContext httpContext,
    BridgeRegistry registry) =>
{
    var instance = registry.Find(id);
    httpContext.Response.Headers.CacheControl = "no-store";
    return instance != null && TryResolveCapturePath(instance, captureId, out var path) && File.Exists(path)
        ? Results.File(path, "image/png", enableRangeProcessing: false)
        : Results.NotFound();
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
    var candidate = Path.GetFullPath(Path.Combine(captureDirectory, $"{captureId}.png"));
    if (!candidate.StartsWith(captureDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return false;

    path = candidate;
    return true;
}
