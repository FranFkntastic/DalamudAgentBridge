using System.Diagnostics;
using System.Text.Json;

namespace DalamudAgentBridge;

public sealed class BridgeRegistry
{
    private readonly string pluginConfigRoot;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BridgeRegistry(IConfiguration configuration)
    {
        pluginConfigRoot = configuration["Bridge:PluginConfigRoot"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs");
    }

    public IReadOnlyList<BridgeInstance> Discover()
    {
        if (!Directory.Exists(pluginConfigRoot))
            return [];

        var instances = new Dictionary<string, BridgeInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginConfigRoot))
        {
            var pluginName = Path.GetFileName(pluginDirectory);
            var bridgeDirectory = Path.Combine(pluginDirectory, "agent-bridge");
            if (!Directory.Exists(bridgeDirectory))
                continue;

            var token = ReadAccessToken(Path.Combine(pluginConfigRoot, $"{pluginName}.json"));
            if (string.IsNullOrWhiteSpace(token))
                continue;

            foreach (var discoveryPath in Directory.EnumerateFiles(bridgeDirectory, "discovery*.json"))
            {
                var discovery = ReadDiscovery(discoveryPath);
                if (discovery == null || !IsProcessAlive(discovery.ProcessId))
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

    private string? ReadAccessToken(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("AgentBridgeAccessToken", out var token)
                ? token.GetString()
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
