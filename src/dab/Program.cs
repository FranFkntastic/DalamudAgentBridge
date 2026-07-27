using System.Text.Json;
using DalamudAgentBridge;
using Microsoft.Extensions.Configuration;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var commandLine = DabCommandLine.Parse(args);
    var configuration = new ConfigurationBuilder()
        .AddEnvironmentVariables("DAB_")
        .Build();
    var registry = new BridgeRegistry(configuration);
    var pipe = new NamedPipeBridgeClient();
    var presentations = new ReviewedControlPresentationService(pipe);
    var logs = new DalamudLogWatcher();
    var client = new AgentBridgeClient(registry, pipe, presentations, logs);
    var deployment = new DevPluginDeploymentService(client);
    var reviewVault = new ReviewVault(configuration);
    var capture = new PluginCaptureService(client, pipe, reviewVault);
    var surfaceCapture = new PluginSurfaceCaptureService(client, capture);
    object result = commandLine.Command switch
    {
        "list" => client.List(),
        "plugins" => await client.GetPluginSurfaceCatalogAsync(null, commandLine.Profile(), commandLine.ProcessId(), cancellation.Token),
        "surfaces" => await client.GetPluginSurfaceCatalogAsync(commandLine.Plugin(), commandLine.Profile(), commandLine.ProcessId(), cancellation.Token),
        "surface-present" => await client.BeginPluginSurfacePresentationAsync(
            commandLine.Plugin(), commandLine.Required("surface"), commandLine.Profile(), commandLine.ProcessId(), cancellation.Token),
        "surface-restore" => await client.RestorePluginSurfacePresentationAsync(
            commandLine.Required("transaction"), commandLine.Profile(), commandLine.ProcessId(), cancellation.Token),
        "surface-capture" => await surfaceCapture.CaptureAsync(
            commandLine.Plugin(), commandLine.Required("surface"), commandLine.Profile(), commandLine.ProcessId(), cancellation.Token),
        "health" => await client.GetHealthAsync(commandLine.Target(), cancellation.Token),
        "manifest" => await client.GetManifestAsync(commandLine.Target(), cancellation.Token),
        "snapshot" => await client.GetSnapshotAsync(commandLine.Target(), cancellation.Token),
        "logs" => client.ReadLogs(commandLine.Target(), commandLine.Long("cursor"), commandLine.Int("limit")),
        "chat" => await client.ReadChatLogAsync(commandLine.Target(), commandLine.Long("cursor"), commandLine.Int("limit"), cancellation.Token),
        "wait" => await client.WaitForSnapshotAsync(
            commandLine.Target(),
            commandLine.Condition("path", "equals"),
            commandLine.Timeout(defaultMilliseconds: 30_000),
            cancellation.Token),
        "act" => await client.ActAndObserveAsync(
            commandLine.Target(),
            new ReviewedControlActionRequest
            {
                SurfaceId = commandLine.Value("surface"),
                ControlId = commandLine.Required("control"),
                Arguments = commandLine.Json("arguments"),
                WaitForCompletion = !commandLine.Flag("no-wait"),
                CompletionCondition = commandLine.OptionalCondition("wait-path", "equals"),
                CompletionTimeoutMilliseconds = commandLine.Int("timeout"),
            },
            cancellation.Token),
        "deploy" => await deployment.DeployAsync(
            commandLine.Target(),
            new DevPluginDeploymentRequest
            {
                SourceDirectory = commandLine.Required("source"),
                ExpectedMainDllSha256 = commandLine.Value("sha256"),
                TimeoutMilliseconds = commandLine.Int("timeout"),
            },
            cancellation.Token),
        "install" => await new PluginLifecycleClient(pipe).InstallAsync(
            registry.Resolve(new BridgeTargetSelector("DalamudAgentBridge", commandLine.Profile(), commandLine.ProcessId())),
            commandLine.Plugin(),
            cancellation.Token),
        "capture" => await capture.CaptureAsync(
            commandLine.Target(),
            new BridgeCommandRequest
            {
                Target = commandLine.Value("target"),
                FullViewport = commandLine.Flag("full-viewport"),
            },
            cancellation.Token),
        _ => throw new ArgumentException($"Unknown dab command '{commandLine.Command}'."),
    };
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = "Operation cancelled or timed out." }, json));
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { success = false, error = exception.Message }, json));
    return 1;
}

internal sealed class DabCommandLine
{
    private readonly Dictionary<string, string?> options;
    private readonly IReadOnlyList<string> positionals;

    private DabCommandLine(string command, Dictionary<string, string?> options, IReadOnlyList<string> positionals)
    {
        Command = command;
        this.options = options;
        this.positionals = positionals;
    }

    public string Command { get; }

    public static DabCommandLine Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            throw new ArgumentException(
                "Usage: dab <list|plugins|surfaces|surface-present|surface-restore|surface-capture|health|manifest|snapshot|logs|chat|wait|act|deploy|install|capture> [plugin] [--profile primary] [options]");
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }
            var key = argument[2..];
            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++index];
            if (!options.TryAdd(key, value))
                throw new ArgumentException($"Option '--{key}' was supplied more than once.");
        }
        return new DabCommandLine(args[0].ToLowerInvariant(), options, positionals);
    }

    public BridgeTargetSelector Target()
    {
        if (positionals.Count != 1)
            throw new ArgumentException($"Command '{Command}' requires exactly one plugin name.");
        return new BridgeTargetSelector(positionals[0], Value("profile") ?? "primary", Int("pid"));
    }

    public string Plugin()
    {
        if (positionals.Count != 1)
            throw new ArgumentException($"Command '{Command}' requires exactly one plugin name.");
        return positionals[0];
    }

    public string Profile() => Value("profile") ?? "primary";

    public int? ProcessId() => Int("pid");

    public string Required(string name) =>
        !string.IsNullOrWhiteSpace(Value(name)) ? Value(name)! : throw new ArgumentException($"Option '--{name}' is required.");

    public string? Value(string name) => options.GetValueOrDefault(name);

    public bool Flag(string name) => options.ContainsKey(name) && options[name] is null;

    public int? Int(string name) => Value(name) is { } value
        ? int.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"Option '--{name}' must be an integer.")
        : null;

    public long? Long(string name) => Value(name) is { } value
        ? long.TryParse(value, out var parsed) ? parsed : throw new ArgumentException($"Option '--{name}' must be an integer.")
        : null;

    public TimeSpan Timeout(int defaultMilliseconds) => TimeSpan.FromMilliseconds(Int("timeout") ?? defaultMilliseconds);

    public JsonElement? Json(string name)
    {
        if (Value(name) is not { } value)
            return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public BridgeWaitCondition Condition(string pathName, string equalsName) =>
        new(Required(pathName), Value(equalsName), options.ContainsKey("exists") ? true : null);

    public BridgeWaitCondition? OptionalCondition(string pathName, string equalsName) =>
        Value(pathName) is { } path ? new BridgeWaitCondition(path, Value(equalsName)) : null;
}
