using System.IO.Pipes;
using System.Text.Json;

namespace DalamudAgentBridge;

public sealed class NamedPipeBridgeClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<PluginBridgeResponse> SendAsync(
        BridgeInstance instance,
        string command,
        BridgeCommandRequest? request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.ToLowerInvariant() switch
        {
            "capture-screen" => TimeSpan.FromSeconds(15),
            "begin-capture-presentation" => TimeSpan.FromSeconds(10),
            _ => DefaultTimeout,
        });
        await using var pipe = new NamedPipeClientStream(
            ".",
            instance.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        var payload = new PluginBridgeRequest
        {
            Token = instance.AccessToken,
            Command = command,
            Target = request?.Target,
            FrameId = request?.FrameId,
            Challenge = request?.Challenge,
            ProofId = request?.ProofId,
            FullViewport = request?.FullViewport ?? false,
            TransactionId = request?.TransactionId,
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(payload, jsonOptions)).ConfigureAwait(false);
        var responseJson = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new IOException("Bridge closed without returning a response.");

        return JsonSerializer.Deserialize<PluginBridgeResponse>(responseJson, jsonOptions) ??
            throw new InvalidDataException("Bridge response JSON was empty.");
    }
}
