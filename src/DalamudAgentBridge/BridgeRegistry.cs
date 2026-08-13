using System.ComponentModel;
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
        var instances = new Dictionary<string, (BridgeInstance Instance, DateTime UpdatedAtUtc)>(StringComparer.OrdinalIgnoreCase);
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
                if (discovery == null || !TryGetCurrentDiscoveryTime(discoveryPath, discovery.ProcessId, out var updatedAtUtc))
                    continue;
                var token = ReadAccessToken(accessTokenPath, discovery.PluginInstanceId);
                if (string.IsNullOrWhiteSpace(token))
                    continue;
                var profile = AgentBridgeProfileIdentity.FromPluginConfigDirectory(pluginDirectory);

                var id = $"{pluginName}-{discovery.ProcessId}";
                var instance = new BridgeInstance
                {
                    Id = id,
                    PluginName = pluginName,
                    PipeName = discovery.PipeName,
                    ProcessId = discovery.ProcessId,
                    SchemaVersion = discovery.SchemaVersion,
                    PluginInstanceId = discovery.PluginInstanceId,
                    AccessToken = token,
                    DiscoveryPath = discoveryPath,
                    PluginInternalName = discovery.PluginInternalName ?? pluginName,
                    RuntimeInstanceId = discovery.RuntimeInstanceId,
                    ProfileId = discovery.ProfileId ?? profile.Id,
                    ProfileAlias = discovery.ProfileAlias ?? profile.Alias,
                    ProtocolVersion = discovery.ProtocolVersion,
                };
                if (!instances.TryGetValue(id, out var existing) || updatedAtUtc > existing.UpdatedAtUtc)
                    instances[id] = (instance, updatedAtUtc);
            }
        }

        return instances.Values.Select(candidate => candidate.Instance)
            .OrderBy(instance => instance.PluginName)
            .ThenBy(instance => instance.ProcessId)
            .ToArray();
    }

    public BridgeInstance? Find(string id) =>
        Discover().FirstOrDefault(instance => string.Equals(instance.Id, id, StringComparison.OrdinalIgnoreCase));

    public BridgeInstance Resolve(BridgeTargetSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector.Plugin);
        var matches = Discover().Where(instance =>
                (string.Equals(instance.PluginInternalName, selector.Plugin, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(instance.PluginName, selector.Plugin, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(instance.Id, selector.Plugin, StringComparison.OrdinalIgnoreCase)) &&
                (selector.ProcessId is null || instance.ProcessId == selector.ProcessId) &&
                (string.IsNullOrWhiteSpace(selector.Profile) ||
                 string.Equals(instance.ProfileAlias, selector.Profile, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(instance.ProfileId, selector.Profile, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"No live bridge matched plugin '{selector.Plugin}'{ProfileSuffix(selector.Profile)}."),
            _ => throw new InvalidOperationException(
                $"Bridge target '{selector.Plugin}' is ambiguous: {string.Join(", ", matches.Select(match => $"{match.Id} ({match.ProfileAlias ?? "unknown profile"})"))}. Supply a profile alias or process ID."),
        };
    }

    private AgentBridgeDiscovery? ReadDiscovery(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentBridgeDiscovery>(File.ReadAllText(path), jsonOptions);
        }
        catch (Exception) when (File.Exists(path))
        {
            return null;
        }
    }

    private static string ProfileSuffix(string? profile) =>
        string.IsNullOrWhiteSpace(profile) ? string.Empty : $" in profile '{profile}'";

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

    private static bool TryGetCurrentDiscoveryTime(string discoveryPath, int processId, out DateTime updatedAtUtc)
    {
        updatedAtUtc = default;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return false;
            updatedAtUtc = File.GetLastWriteTimeUtc(discoveryPath);
            return updatedAtUtc >= process.StartTime.ToUniversalTime().AddSeconds(-5);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
