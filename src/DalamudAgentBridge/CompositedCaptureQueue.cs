using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DalamudAgentBridge;

/// <summary>
/// Holds short-lived, authenticated capture requests until FFXIV itself is foreground.
/// No screenshot bytes are created while a request is queued.
/// </summary>
public sealed class CompositedCaptureQueue
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, CompositedCaptureRequest> requests = new(StringComparer.Ordinal);
    private readonly CompositedGameWindowCaptureService captureService;
    private readonly ReviewVault reviewVault;

    public CompositedCaptureQueue(CompositedGameWindowCaptureService captureService, ReviewVault reviewVault)
    {
        this.captureService = captureService;
        this.reviewVault = reviewVault;
    }

    public CompositedCaptureRequest Queue(int processId)
    {
        PurgeExpired();
        var request = new CompositedCaptureRequest(Guid.NewGuid().ToString("N"), processId, "queued", "Waiting for the FFXIV client to become foreground.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.Add(RequestLifetime), null, null, null);
        if (!requests.TryAdd(request.RequestId, request))
            throw new InvalidOperationException("Could not allocate a composited-capture request.");
        _ = Task.Run(() => ProcessAsync(request));
        return request;
    }

    public bool TryGet(string requestId, out CompositedCaptureRequest request)
    {
        PurgeExpired();
        return requests.TryGetValue(requestId, out request!);
    }

    private async Task ProcessAsync(CompositedCaptureRequest initial)
    {
        while (DateTimeOffset.UtcNow < initial.ExpiresAtUtc)
        {
            if (!captureService.IsForegroundTarget(initial.ProcessId))
            {
                await Task.Delay(125).ConfigureAwait(false);
                continue;
            }

            byte[]? pngBytes = null;
            try
            {
                var capture = captureService.Capture(initial.ProcessId);
                pngBytes = capture.PngBytes;
                var receipt = new BridgeCaptureReceipt
                {
                    SchemaVersion = 1,
                    CaptureId = Guid.NewGuid().ToString("N"),
                    FileName = "composited-window-memory",
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Width = capture.Width,
                    Height = capture.Height,
                    Sha256 = Convert.ToHexString(SHA256.HashData(pngBytes)),
                    ProcessId = initial.ProcessId,
                    Scope = "CompositedGameWindow",
                };
                var review = reviewVault.Store(receipt, pngBytes);
                requests[initial.RequestId] = initial with
                {
                    State = "completed",
                    Message = "Foreground FFXIV client area captured from the final compositor output.",
                    Receipt = receipt,
                    Review = review,
                    ImageUrl = $"/api/reviews/{review.Id}.png",
                };
                return;
            }
            catch (InvalidOperationException)
            {
                // Focus can change between the check and the GDI copy. Keep waiting without capturing another window.
            }
            catch (Exception ex)
            {
                requests[initial.RequestId] = initial with { State = "failed", Message = $"Composited capture failed: {ex.Message}" };
                return;
            }
            finally
            {
                if (pngBytes != null)
                    CryptographicOperations.ZeroMemory(pngBytes);
            }
            await Task.Delay(125).ConfigureAwait(false);
        }

        requests[initial.RequestId] = initial with { State = "expired", Message = "Composited capture request expired before FFXIV became foreground." };
    }

    private void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - ResultLifetime;
        foreach (var pair in requests)
        {
            if (pair.Value.CreatedAtUtc < cutoff)
                requests.TryRemove(pair.Key, out _);
        }
    }
}

public sealed record CompositedCaptureRequest(
    string RequestId,
    int ProcessId,
    string State,
    string Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    BridgeCaptureReceipt? Receipt,
    ReviewCapture? Review,
    string? ImageUrl);
