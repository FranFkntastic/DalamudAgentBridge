using System.Text.Json;

namespace DalamudAgentBridge;

/// <summary>Samples a short ordered sequence through the existing authenticated full-viewport capture path.</summary>
public sealed class DiagnosticClipService
{
    public const int MinimumFrames = 2;
    public const int MaximumFrames = 12;
    public const int MinimumIntervalMilliseconds = 250;
    public const int MaximumIntervalMilliseconds = 5000;
    public const int MaximumClipSpanMilliseconds = 60_000;

    private readonly AgentBridgeClient client;
    private readonly Func<BridgeInstance, CancellationToken, Task<PluginCaptureReviewReceipt>> captureFrame;

    public DiagnosticClipService(AgentBridgeClient client, PluginCaptureService capture)
        : this(client, (instance, cancellationToken) => capture.CaptureAsync(
            instance,
            new BridgeCommandRequest { FullViewport = true },
            cancellationToken))
    {
    }

    internal DiagnosticClipService(
        AgentBridgeClient client,
        Func<BridgeInstance, CancellationToken, Task<PluginCaptureReviewReceipt>> captureFrame)
    {
        this.client = client;
        this.captureFrame = captureFrame;
    }

    public Task<DiagnosticClipReceipt> CaptureAsync(
        BridgeTargetSelector selector,
        DiagnosticClipRequest request,
        CancellationToken cancellationToken) =>
        CaptureAsync(client.Resolve(selector), request, cancellationToken);

    public async Task<DiagnosticClipReceipt> CaptureAsync(
        BridgeInstance instance,
        DiagnosticClipRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var startedAt = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaximumClipSpanMilliseconds);
        var frames = new List<DiagnosticClipFrame>(request.FrameCount);
        string? failure = null;
        for (var index = 0; index < request.FrameCount; index++)
        {
            if (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                failure = $"The diagnostic clip reached its {MaximumClipSpanMilliseconds} ms deadline before frame {index}.";
                break;
            }
            var requestedAt = DateTimeOffset.UtcNow;
            try
            {
                var situation = CompactSituation(await client.GetSituationAsync(instance, deadline.Token).ConfigureAwait(false));
                var capture = await captureFrame(instance, deadline.Token).ConfigureAwait(false);
                frames.Add(new DiagnosticClipFrame(index, requestedAt, situation, capture));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                failure = $"The diagnostic clip reached its {MaximumClipSpanMilliseconds} ms deadline while capturing frame {index}.";
                break;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException or OperationCanceledException)
            {
                failure = $"Frame {index} failed: {exception.Message}";
                break;
            }

            if (index + 1 < request.FrameCount)
            {
                var nextFrameAt = startedAt.AddMilliseconds((index + 1L) * request.IntervalMilliseconds);
                var delay = nextFrameAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException)
                    {
                        failure = $"The diagnostic clip reached its {MaximumClipSpanMilliseconds} ms deadline before frame {index + 1}.";
                        break;
                    }
                }
            }
        }

        return new DiagnosticClipReceipt(
            AgentBridgeClient.ToView(instance),
            startedAt,
            DateTimeOffset.UtcNow,
            request.FrameCount,
            request.IntervalMilliseconds,
            frames,
            failure);
    }

    internal static void Validate(DiagnosticClipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FrameCount is < MinimumFrames or > MaximumFrames)
            throw new ArgumentOutOfRangeException(nameof(request), $"FrameCount must be between {MinimumFrames} and {MaximumFrames}.");
        if (request.IntervalMilliseconds is < MinimumIntervalMilliseconds or > MaximumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(request), $"IntervalMilliseconds must be between {MinimumIntervalMilliseconds} and {MaximumIntervalMilliseconds}.");
        if ((request.FrameCount - 1L) * request.IntervalMilliseconds > MaximumClipSpanMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(request), $"The requested diagnostic clip may span at most {MaximumClipSpanMilliseconds} milliseconds.");
    }

    internal static JsonElement CompactSituation(JsonElement situation)
    {
        static JsonElement? Property(JsonElement value, string name) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
                ? property.Clone()
                : null;
        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = Property(situation, "schemaVersion"),
            capturedAtUtc = Property(situation, "capturedAtUtc"),
            available = Property(situation, "available"),
            client = Property(situation, "client"),
            character = Property(situation, "character"),
            activeConditions = Property(situation, "activeConditions"),
            navigation = Property(situation, "navigation"),
        });
    }
}
