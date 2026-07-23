using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudAgentBridge;

/// <summary>Deploys a built dev-plugin directory and proves the exact hot-reloaded assembly through its new manifest.</summary>
public sealed class DevPluginDeploymentService
{
    private readonly AgentBridgeClient client;

    public DevPluginDeploymentService(AgentBridgeClient client) => this.client = client;

    public async Task<DevPluginDeploymentReceipt> DeployAsync(
        BridgeTargetSelector selector,
        DevPluginDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceDirectory);
        var startedAt = DateTimeOffset.UtcNow;
        var beforeInstance = client.Resolve(selector);
        var beforeManifest = await client.GetManifestAsync(beforeInstance, cancellationToken).ConfigureAwait(false);
        var sourceDirectory = Path.GetFullPath(request.SourceDirectory);
        var mainDllName = $"{beforeManifest.Runtime.PluginInternalName}.dll";
        var manifestName = $"{beforeManifest.Runtime.PluginInternalName}.json";
        var sourceDll = Path.Combine(sourceDirectory, mainDllName);
        var sourceManifest = Path.Combine(sourceDirectory, manifestName);
        ValidateSource(beforeManifest.Runtime.PluginInternalName, sourceDirectory, sourceDll, sourceManifest, request.ExpectedMainDllSha256);
        var targetDirectory = Path.GetDirectoryName(beforeManifest.Runtime.MainDllPath)
            ?? throw new InvalidOperationException("The loaded bridge manifest did not identify its plugin directory.");
        if (targetDirectory.Contains($"{Path.DirectorySeparatorChar}installedPlugins{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected plugin is an installed package. Use the transactional installed-plugin replacement endpoint instead.");
        if (string.Equals(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar), targetDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The build source and loaded dev-plugin directory are the same path; no deployment is necessary.");

        var sourceHash = HashFile(sourceDll);
        if (string.Equals(sourceHash, beforeManifest.Runtime.MainDllSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new DevPluginDeploymentReceipt(
                AgentBridgeClient.ToView(beforeInstance),
                AgentBridgeClient.ToView(beforeInstance),
                sourceDirectory,
                targetDirectory,
                beforeManifest.Runtime.MainDllSha256,
                beforeManifest.Runtime.MainDllSha256,
                beforeManifest.Runtime.MainDllSha256,
                beforeManifest.Runtime.RuntimeInstanceId,
                beforeManifest.Runtime.RuntimeInstanceId,
                startedAt,
                DateTimeOffset.UtcNow,
                Reloaded: false);
        }
        var backupDirectory = Path.Combine(Path.GetTempPath(), "DalamudAgentBridge", "dev-plugin-backups", Guid.NewGuid().ToString("N"));
        var originalFiles = Directory.Exists(targetDirectory)
            ? Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(targetDirectory, file))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(backupDirectory);
        CopyDirectory(targetDirectory, backupDirectory, overwrite: false);
        try
        {
            CopyBuildWithMainDllLast(sourceDirectory, targetDirectory, mainDllName);
            var installedHash = HashFile(Path.Combine(targetDirectory, mainDllName));
            if (!string.Equals(sourceHash, installedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The deployed dev-plugin DLL hash does not match the selected build.");

            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMilliseconds ?? 20_000, 1_000, 120_000));
            var (afterInstance, afterManifest) = await WaitForReloadAsync(
                selector,
                beforeManifest.Runtime.RuntimeInstanceId,
                sourceHash,
                timeout,
                cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(backupDirectory);
            return new DevPluginDeploymentReceipt(
                AgentBridgeClient.ToView(beforeInstance),
                AgentBridgeClient.ToView(afterInstance),
                sourceDirectory,
                targetDirectory,
                beforeManifest.Runtime.MainDllSha256,
                installedHash,
                afterManifest.Runtime.MainDllSha256,
                beforeManifest.Runtime.RuntimeInstanceId,
                afterManifest.Runtime.RuntimeInstanceId,
                startedAt,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            RestoreDirectory(backupDirectory, targetDirectory, originalFiles);
            throw;
        }
    }

    private async Task<(BridgeInstance Instance, Franthropy.Dalamud.AgentBridge.AgentBridgeManifest Manifest)> WaitForReloadAsync(
        BridgeTargetSelector selector,
        string previousRuntimeInstanceId,
        string expectedHash,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Exception? lastError = null;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                var instance = client.Resolve(selector);
                var manifest = await client.GetManifestAsync(instance, deadline.Token).ConfigureAwait(false);
                if (!string.Equals(manifest.Runtime.RuntimeInstanceId, previousRuntimeInstanceId, StringComparison.Ordinal) &&
                    string.Equals(manifest.Runtime.MainDllSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return (instance, manifest);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or KeyNotFoundException)
            {
                lastError = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !deadline.IsCancellationRequested)
            {
                // A disappearing pipe can consume its own per-command timeout while Dalamud reloads.
                // Keep polling until the deployment deadline instead of treating that transient as caller cancellation.
                lastError = exception;
            }
            try { await Task.Delay(100, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The dev plugin did not advertise the expected reloaded hash before the timeout. Last error: {lastError?.Message ?? "none"}");
            }
        }
    }

    private static void ValidateSource(string internalName, string sourceDirectory, string sourceDll, string sourceManifest, string? expectedHash)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Build directory '{sourceDirectory}' does not exist.");
        if (!File.Exists(sourceDll) || !File.Exists(sourceManifest))
            throw new InvalidDataException($"Build directory must contain {internalName}.dll and {internalName}.json.");
        using var manifest = JsonDocument.Parse(File.ReadAllText(sourceManifest));
        if (!manifest.RootElement.TryGetProperty("InternalName", out var manifestName) ||
            !string.Equals(manifestName.GetString(), internalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Build manifest internal name does not match the selected plugin.");
        var actualHash = HashFile(sourceDll);
        if (!string.IsNullOrWhiteSpace(expectedHash) && !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Build DLL hash does not match the caller's expected hash.");
    }

    private static void CopyBuildWithMainDllLast(string source, string destination, string mainDllName)
    {
        Directory.CreateDirectory(destination);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .OrderBy(file => string.Equals(Path.GetRelativePath(source, file), mainDllName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToArray();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            if (string.Equals(relative, mainDllName, StringComparison.OrdinalIgnoreCase))
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        if (!Directory.Exists(source))
            return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private static void RestoreDirectory(string backup, string destination, IReadOnlySet<string> originalFiles)
    {
        if (!Directory.Exists(backup))
            return;
        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(destination, file);
            if (!originalFiles.Contains(relative))
                File.Delete(file);
        }
        CopyDirectory(backup, destination, overwrite: true);
        TryDeleteDirectory(backup);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
