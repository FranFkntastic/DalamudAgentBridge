using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudAgentBridge;

public sealed class LocalPluginBuildReplacementService
{
    private readonly IPluginLifecycleClient lifecycleClient;

    public LocalPluginBuildReplacementService(IPluginLifecycleClient lifecycleClient) => this.lifecycleClient = lifecycleClient;

    public async Task<LocalPluginBuildReplacementReceipt> ReplaceAsync(
        BridgeInstance instance,
        string internalName,
        LocalPluginBuildReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceDirectory);
        if (string.Equals(internalName, instance.PluginInternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The bridge cannot replace itself while serving a request.");

        var startedAt = DateTimeOffset.UtcNow;
        var snapshot = await lifecycleClient.ListAsync(instance, cancellationToken).ConfigureAwait(false);
        var plugin = snapshot.Plugins.SingleOrDefault(candidate =>
            string.Equals(candidate.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Plugin '{internalName}' is not installed.");
        if (plugin.IsDev)
            throw new InvalidOperationException("Dev plugins already reload from their configured deployment path and cannot be replaced through the installed-package endpoint.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedCurrentVersion) &&
            !string.Equals(plugin.Version, request.ExpectedCurrentVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Installed version '{plugin.Version}' does not match expected version '{request.ExpectedCurrentVersion}'.");

        var sourceDirectory = Path.GetFullPath(request.SourceDirectory);
        var sourceDll = Path.Combine(sourceDirectory, $"{plugin.InternalName}.dll");
        var sourceManifest = Path.Combine(sourceDirectory, $"{plugin.InternalName}.json");
        ValidateSource(plugin.InternalName, sourceDirectory, sourceDll, sourceManifest, request.ExpectedMainDllSha256);

        var installedDirectory = ResolveInstalledPluginDirectory(instance, plugin);
        if (string.Equals(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar), installedDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The local build source and installed plugin directory must be different.");
        var installedDll = Path.Combine(installedDirectory, $"{plugin.InternalName}.dll");
        if (!File.Exists(installedDll))
            throw new FileNotFoundException("The installed plugin DLL could not be located.", installedDll);

        var previousHash = HashFile(installedDll);
        var backupDirectory = Path.Combine(Path.GetTempPath(), "DalamudAgentBridge", "plugin-replacements", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory)!);
        CopyDirectory(installedDirectory, backupDirectory, overwrite: false);

        try
        {
            if (plugin.IsLoaded)
                await lifecycleClient.SetEnabledAsync(instance, plugin.InternalName, false, cancellationToken).ConfigureAwait(false);

            CopyDirectory(
                sourceDirectory,
                installedDirectory,
                overwrite: true,
                request.PreserveInstalledManifest ? $"{plugin.InternalName}.json" : null);
            var installedHash = HashFile(installedDll);
            var expectedHash = HashFile(sourceDll);
            if (!string.Equals(installedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installed DLL hash does not match the selected local build.");

            if (request.EnableAfterReplacement)
                await lifecycleClient.SetEnabledAsync(instance, plugin.InternalName, true, cancellationToken).ConfigureAwait(false);

            var after = await lifecycleClient.ListAsync(instance, cancellationToken).ConfigureAwait(false);
            var installed = after.Plugins.Single(candidate =>
                string.Equals(candidate.InternalName, plugin.InternalName, StringComparison.OrdinalIgnoreCase));
            TryDeleteDirectory(backupDirectory);
            return new LocalPluginBuildReplacementReceipt(
                plugin.InternalName,
                plugin.Version,
                sourceDirectory,
                installedDirectory,
                previousHash,
                installedHash,
                plugin.IsLoaded,
                installed.IsLoaded,
                startedAt,
                DateTimeOffset.UtcNow);
        }
        catch (Exception replacementException)
        {
            try
            {
                await RollbackAsync(instance, plugin, installedDirectory, backupDirectory).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    $"Plugin replacement failed and automatic rollback also failed. The original backup remains at '{backupDirectory}'.",
                    replacementException,
                    rollbackException);
            }
            throw;
        }
    }

    public static string ResolveInstalledPluginDirectory(BridgeInstance instance, InstalledPluginState plugin)
    {
        var discoveryDirectory = Directory.GetParent(instance.DiscoveryPath)
            ?? throw new InvalidOperationException("Bridge discovery path has no parent directory.");
        var pluginConfigDirectory = discoveryDirectory.Parent
            ?? throw new InvalidOperationException("Bridge discovery path is outside a plugin configuration directory.");
        var pluginConfigsDirectory = pluginConfigDirectory.Parent
            ?? throw new InvalidOperationException("Bridge discovery path is outside a launcher profile.");
        var profileDirectory = pluginConfigsDirectory.Parent
            ?? throw new InvalidOperationException("Bridge discovery path is outside a launcher profile.");
        var installedRoot = Path.GetFullPath(Path.Combine(profileDirectory.FullName, "installedPlugins"));
        var destination = Path.GetFullPath(Path.Combine(installedRoot, plugin.InternalName, plugin.Version));
        if (!destination.StartsWith(installedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved plugin destination escaped the launcher installed-plugin directory.");
        return destination;
    }

    private async Task RollbackAsync(
        BridgeInstance instance,
        InstalledPluginState plugin,
        string installedDirectory,
        string backupDirectory)
    {
        var current = await lifecycleClient.ListAsync(instance, CancellationToken.None).ConfigureAwait(false);
        var state = current.Plugins.SingleOrDefault(candidate =>
            string.Equals(candidate.InternalName, plugin.InternalName, StringComparison.OrdinalIgnoreCase));
        if (state?.IsLoaded == true)
            await lifecycleClient.SetEnabledAsync(instance, plugin.InternalName, false, CancellationToken.None).ConfigureAwait(false);
        if (Directory.Exists(installedDirectory))
            Directory.Delete(installedDirectory, recursive: true);
        CopyDirectory(backupDirectory, installedDirectory, overwrite: false);
        if (plugin.IsLoaded)
            await lifecycleClient.SetEnabledAsync(instance, plugin.InternalName, true, CancellationToken.None).ConfigureAwait(false);
        TryDeleteDirectory(backupDirectory);
    }

    private static void ValidateSource(
        string internalName,
        string sourceDirectory,
        string sourceDll,
        string sourceManifest,
        string? expectedHash)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Local plugin build directory '{sourceDirectory}' does not exist.");
        if (!File.Exists(sourceDll))
            throw new FileNotFoundException("Local plugin build does not contain its main DLL.", sourceDll);
        if (!File.Exists(sourceManifest))
            throw new FileNotFoundException("Local plugin build does not contain its manifest.", sourceManifest);
        using var manifest = JsonDocument.Parse(File.ReadAllText(sourceManifest));
        if (!manifest.RootElement.TryGetProperty("InternalName", out var manifestName) ||
            !string.Equals(manifestName.GetString(), internalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local plugin manifest internal name does not match the requested plugin.");
        var actualHash = HashFile(sourceDll);
        if (!string.IsNullOrWhiteSpace(expectedHash) &&
            !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local plugin DLL hash does not match the caller's expected hash.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination, bool overwrite, string? excludedRelativePath = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            if (string.Equals(relativePath, excludedRelativePath, StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A completed install does not become unsafe because its temporary backup could not be cleaned up.
        }
        catch (UnauthorizedAccessException)
        {
            // The next maintenance pass may remove an otherwise harmless retained backup.
        }
    }
}
