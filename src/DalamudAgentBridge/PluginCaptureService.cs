using System.Security.Cryptography;
using System.Text.Json;
using Franthropy.Dalamud.AgentBridge;

namespace DalamudAgentBridge;

/// <summary>Imports one plugin-produced encrypted capture into the short-lived encrypted review vault.</summary>
public sealed class PluginCaptureService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly AgentBridgeClient client;
    private readonly NamedPipeBridgeClient pipe;
    private readonly ReviewVault reviewVault;

    public PluginCaptureService(AgentBridgeClient client, NamedPipeBridgeClient pipe, ReviewVault reviewVault)
    {
        this.client = client;
        this.pipe = pipe;
        this.reviewVault = reviewVault;
    }

    public Task<PluginCaptureReviewReceipt> CaptureAsync(
        BridgeTargetSelector selector,
        BridgeCommandRequest? request,
        CancellationToken cancellationToken) =>
        CaptureAsync(client.Resolve(selector), request, cancellationToken);

    public async Task<PluginCaptureReviewReceipt> CaptureAsync(
        BridgeInstance instance,
        BridgeCommandRequest? request,
        CancellationToken cancellationToken)
    {
        BridgeCaptureTransactionReceipt? transaction = null;
        var transactionFinished = false;
        BridgeCaptureReceipt? receipt = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request?.Target))
            {
                var begin = await pipe.SendAsync(instance, "begin-capture-presentation", new BridgeCommandRequest
                {
                    Target = request.Target,
                }, cancellationToken).ConfigureAwait(false);
                if (!begin.Success || begin.Receipt is not { } transactionElement)
                    throw new InvalidOperationException(begin.Message);
                transaction = transactionElement.Deserialize<BridgeCaptureTransactionReceipt>(JsonOptions)
                    ?? throw new InvalidDataException("Bridge returned an invalid capture presentation receipt.");
                if (string.IsNullOrWhiteSpace(transaction.TransactionId) || transaction.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    throw new InvalidDataException("Bridge returned an expired capture presentation receipt.");
            }

            var captureCommand = transaction is null && !string.IsNullOrWhiteSpace(request?.TransactionId)
                ? "capture-plugin-surface"
                : "capture-screen";
            var response = await pipe.SendAsync(instance, captureCommand, new BridgeCommandRequest
            {
                FullViewport = request?.FullViewport ?? false,
                TransactionId = transaction?.TransactionId ?? request?.TransactionId,
            }, cancellationToken).ConfigureAwait(false);
            if (!response.Success || response.Receipt is not { } receiptElement)
                throw new InvalidOperationException(response.Message);
            receipt = receiptElement.Deserialize<BridgeCaptureReceipt>(JsonOptions);

            if (transaction is not null)
            {
                var complete = await pipe.SendAsync(instance, "complete-capture-presentation", new BridgeCommandRequest
                {
                    TransactionId = transaction.TransactionId,
                }, cancellationToken).ConfigureAwait(false);
                if (!complete.Success)
                    throw new InvalidOperationException(complete.Message);
                transactionFinished = true;
            }
        }
        finally
        {
            if (transaction is not null && !transactionFinished)
            {
                try
                {
                    await pipe.SendAsync(instance, "cancel-capture-presentation", new BridgeCommandRequest
                    {
                        TransactionId = transaction.TransactionId,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* Plugin-side expiry still restores the previous window state. */ }
            }
        }

        if (receipt == null ||
            receipt.ProcessId != instance.ProcessId ||
            receipt.Width is < 1 or > 16384 ||
            receipt.Height is < 1 or > 16384 ||
            !string.Equals(receipt.FileName, $"{receipt.CaptureId}.bin", StringComparison.Ordinal) ||
            !TryResolveCapturePath(instance, receipt.CaptureId, out var capturePath) ||
            !File.Exists(capturePath))
            throw new InvalidDataException("Bridge returned an invalid capture receipt.");

        byte[] pngBytes;
        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(capturePath, cancellationToken).ConfigureAwait(false);
            try { pngBytes = AgentBridgeDataProtection.UnprotectBytes(encryptedBytes, instance.PluginInstanceId); }
            finally { CryptographicOperations.ZeroMemory(encryptedBytes); }
        }
        finally
        {
            File.Delete(capturePath);
        }

        try
        {
            var actualSha256 = Convert.ToHexString(SHA256.HashData(pngBytes));
            if (!string.Equals(actualSha256, receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bridge capture hash verification failed.");
            var review = reviewVault.Store(receipt, pngBytes);
            return new PluginCaptureReviewReceipt(
                AgentBridgeClient.ToView(instance),
                receipt,
                review,
                $"/api/reviews/{review.Id}.png");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pngBytes);
        }
    }

    private static bool TryResolveCapturePath(BridgeInstance instance, string captureId, out string path)
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
}
