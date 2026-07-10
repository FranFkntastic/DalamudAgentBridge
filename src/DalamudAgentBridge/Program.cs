using DalamudAgentBridge;

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

app.MapFallbackToFile("index.html");
app.Run();
