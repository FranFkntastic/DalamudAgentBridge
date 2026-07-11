using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudAgentBridge;

public sealed class UnfocusedReviewCaptureQueue
{
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, UnfocusedReviewCaptureRequest> requests = new(StringComparer.Ordinal);
    private readonly NamedPipeBridgeClient bridgeClient;
    private readonly WindowsGraphicsCaptureService captureService;
    private readonly ReviewVault reviewVault;
    private readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public UnfocusedReviewCaptureQueue(NamedPipeBridgeClient bridgeClient, WindowsGraphicsCaptureService captureService, ReviewVault reviewVault)
    {
        this.bridgeClient = bridgeClient;
        this.captureService = captureService;
        this.reviewVault = reviewVault;
    }

    public UnfocusedReviewCaptureRequest Queue(BridgeInstance instance, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        PurgeExpired();
        var now = DateTimeOffset.UtcNow;
        var request = new UnfocusedReviewCaptureRequest(
            Guid.NewGuid().ToString("N"), instance.ProcessId, instance.PluginName, "preparing",
            "Requesting a frame-confirmed main-viewport presentation from the plugin.", now, now.AddSeconds(20), null, null, null);
        if (!requests.TryAdd(request.RequestId, request))
            throw new InvalidOperationException("Could not allocate an unfocused review capture request.");
        _ = Task.Run(() => ProcessAsync(instance, target, request));
        return request;
    }

    public bool TryGet(string requestId, out UnfocusedReviewCaptureRequest request)
    {
        PurgeExpired();
        return requests.TryGetValue(requestId, out request!);
    }

    private async Task ProcessAsync(BridgeInstance instance, string target, UnfocusedReviewCaptureRequest initial)
    {
        BridgeCaptureTransactionReceipt? transaction = null;
        ReviewCapture? review = null;
        byte[]? pngBytes = null;
        var transactionFinished = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try
        {
            var begin = await bridgeClient.SendAsync(instance, "begin-capture-presentation", new BridgeCommandRequest
            {
                Target = target,
            }, timeout.Token).ConfigureAwait(false);
            if (!begin.Success || begin.Receipt is not { } receiptElement)
                throw new UnfocusedCaptureException("preparing", begin.Message);
            transaction = receiptElement.Deserialize<BridgeCaptureTransactionReceipt>(jsonOptions) ??
                throw new UnfocusedCaptureException("preparing", "The plugin returned an invalid capture transaction receipt.");
            if (string.IsNullOrWhiteSpace(transaction.TransactionId) || transaction.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new UnfocusedCaptureException("preparing", "The plugin returned an expired capture transaction.");

            Update(initial, "capturing", "The reviewed frame is ready; capturing the unfocused FFXIV main window.");
            var remaining = transaction.ExpiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new UnfocusedCaptureException("capturing", "The capture transaction expired before WGC began.");
            var capture = await captureService.CaptureAsync(instance.ProcessId, timeout.Token, remaining).ConfigureAwait(false);
            pngBytes = capture.PngBytes;

            Update(initial, "storing", "Encrypting the captured frame in the local review vault.");
            var receipt = new BridgeCaptureReceipt
            {
                SchemaVersion = 1,
                CaptureId = Guid.NewGuid().ToString("N"),
                FileName = "windows-graphics-capture-memory",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Width = capture.Width,
                Height = capture.Height,
                Sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)),
                ProcessId = instance.ProcessId,
                Scope = "PluginInclusiveGameWindow",
                CaptureMethod = "WindowsGraphicsCapture",
                TargetPlugin = instance.PluginName,
                TransactionId = transaction.TransactionId,
                FrameId = transaction.FrameId,
            };
            review = reviewVault.Store(receipt, pngBytes);

            Update(initial, "restoring", "Capture stored; restoring the plugin window's prior state.");
            var complete = await bridgeClient.SendAsync(instance, "complete-capture-presentation", new BridgeCommandRequest
            {
                TransactionId = transaction.TransactionId,
            }, timeout.Token).ConfigureAwait(false);
            if (!complete.Success)
                throw new UnfocusedCaptureException("restoring", complete.Message);
            transactionFinished = true;

            requests[initial.RequestId] = initial with
            {
                State = "completed",
                Message = "Plugin-inclusive FFXIV review captured without changing the foreground application.",
                Receipt = receipt,
                Review = review,
                ImageUrl = $"/api/reviews/{review.Id}.png",
            };
        }
        catch (Exception ex)
        {
            if (review != null)
                reviewVault.Delete(review.Id);
            var stage = ex is UnfocusedCaptureException staged ? staged.Stage : requests.GetValueOrDefault(initial.RequestId, initial).State;
            requests[initial.RequestId] = initial with { State = "failed", Message = $"Unfocused capture failed during {stage}: {ex.Message}" };
        }
        finally
        {
            if (pngBytes != null)
                CryptographicOperations.ZeroMemory(pngBytes);
            if (transaction != null && !transactionFinished)
            {
                try
                {
                    await bridgeClient.SendAsync(instance, "cancel-capture-presentation", new BridgeCommandRequest
                    {
                        TransactionId = transaction.TransactionId,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* The plugin-side expiry still restores state if the pipe is unavailable. */ }
            }
        }
    }

    private void Update(UnfocusedReviewCaptureRequest initial, string state, string message) =>
        requests[initial.RequestId] = requests.GetValueOrDefault(initial.RequestId, initial) with { State = state, Message = message };

    private void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - ResultLifetime;
        foreach (var pair in requests)
            if (pair.Value.CreatedAtUtc < cutoff)
                requests.TryRemove(pair.Key, out _);
    }

    private sealed class UnfocusedCaptureException : Exception
    {
        public UnfocusedCaptureException(string stage, string message) : base(message) => Stage = stage;
        public string Stage { get; }
    }
}

public sealed record UnfocusedReviewCaptureRequest(
    string RequestId,
    int ProcessId,
    string TargetPlugin,
    string State,
    string Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    BridgeCaptureReceipt? Receipt,
    ReviewCapture? Review,
    string? ImageUrl);
