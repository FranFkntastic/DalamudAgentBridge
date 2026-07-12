using System.Diagnostics;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

public sealed class BridgeRegistry
{
    private readonly IReadOnlyList<string> pluginConfigRoots;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BridgeRegistry(IConfiguration configuration)
    {
        pluginConfigRoots = ResolvePluginConfigRoots(
            configuration,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
    }

    public static IReadOnlyList<string> ResolvePluginConfigRoots(IConfiguration configuration, string applicationDataRoot)
    {
        var configuredRoots = configuration["Bridge:PluginConfigRoots"];
        if (!string.IsNullOrWhiteSpace(configuredRoots))
            return configuredRoots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var configuredRoot = configuration["Bridge:PluginConfigRoot"];
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return [Path.GetFullPath(configuredRoot)];
        if (!Directory.Exists(applicationDataRoot))
            return [];
        return Directory.EnumerateDirectories(applicationDataRoot, "XIVLauncher*")
            .Select(path => Path.Combine(path, "pluginConfigs"))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<BridgeInstance> Discover()
    {
        var instances = new Dictionary<string, BridgeInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var pluginConfigRoot in pluginConfigRoots.Where(Directory.Exists))
        foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginConfigRoot))
        {
            var pluginName = Path.GetFileName(pluginDirectory);
            var bridgeDirectory = Path.Combine(pluginDirectory, "agent-bridge");
            if (!Directory.Exists(bridgeDirectory))
                continue;

            var accessTokenPath = Path.Combine(pluginConfigRoot, $"{pluginName}.json");

            foreach (var discoveryPath in Directory.EnumerateFiles(bridgeDirectory, "discovery*.json"))
            {
                var discovery = ReadDiscovery(discoveryPath);
                if (discovery == null || !IsProcessAlive(discovery.ProcessId))
                    continue;
                var token = ReadAccessToken(accessTokenPath, discovery.PluginInstanceId);
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                var id = $"{pluginName}-{discovery.ProcessId}";
                instances[id] = new BridgeInstance
                {
                    Id = id,
                    PluginName = pluginName,
                    PipeName = discovery.PipeName,
                    ProcessId = discovery.ProcessId,
                    SchemaVersion = discovery.SchemaVersion,
                    PluginInstanceId = discovery.PluginInstanceId,
                    AccessToken = token,
                    DiscoveryPath = discoveryPath,
                };
            }
        }

        return instances.Values.OrderBy(instance => instance.PluginName).ThenBy(instance => instance.ProcessId).ToArray();
    }

    public BridgeInstance? Find(string id) =>
        Discover().FirstOrDefault(instance => string.Equals(instance.Id, id, StringComparison.OrdinalIgnoreCase));

    private BridgeDiscovery? ReadDiscovery(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeDiscovery>(File.ReadAllText(path), jsonOptions);
        }
        catch (Exception) when (File.Exists(path))
        {
            return null;
        }
    }

    private string? ReadAccessToken(string path, string pluginInstanceId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("AgentBridgeProtectedAccessToken", out var protectedToken) &&
                !string.IsNullOrWhiteSpace(protectedToken.GetString()))
            {
                return AgentBridgeDataProtection.UnprotectToken(protectedToken.GetString()!, pluginInstanceId);
            }

            return document.RootElement.TryGetProperty("AgentBridgeAccessToken", out var legacyToken)
                ? legacyToken.GetString()
                : null;
        }
        catch (Exception) when (File.Exists(path))
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
