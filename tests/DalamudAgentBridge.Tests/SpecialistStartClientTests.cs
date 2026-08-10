using System.IO.Pipes;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class SpecialistStartClientTests
{
    [Fact]
    public async Task StartSpecialistUsesThePolicyCamelCaseEnvelope()
    {
        var pipeName = $"dab-specialist-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            var request = JsonDocument.Parse(await reader.ReadLineAsync() ?? throw new InvalidDataException("Client did not send a request.")).RootElement.Clone();
            await writer.WriteLineAsync("""{"success":true,"message":"Started"}""");
            return request;
        });
        var client = new AgentBridgeClient(null!, new NamedPipeBridgeClient(), null!, null!);
        var instance = new BridgeInstance
        {
            Id = "test-1",
            PluginName = "DalamudAgentBridge",
            PipeName = pipeName,
            ProcessId = Environment.ProcessId,
            SchemaVersion = 1,
            PluginInstanceId = "test",
            AccessToken = "test",
            DiscoveryPath = "test",
            PluginInternalName = "DalamudAgentBridge",
        };

        await client.StartSpecialistAsync(
            instance,
            new SpecialistStartRequest("test.run", JsonSerializer.SerializeToElement(new { value = "work" }), 90),
            CancellationToken.None);
        var sent = await serverTask;

        Assert.Equal("start-specialist", sent.GetProperty("command").GetString());
        Assert.Equal("test.run", sent.GetProperty("target").GetString());
        var arguments = sent.GetProperty("arguments");
        Assert.Equal(90, arguments.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal("work", arguments.GetProperty("parameters").GetProperty("value").GetString());
        Assert.False(arguments.TryGetProperty("TimeoutSeconds", out _));
        Assert.False(arguments.TryGetProperty("Parameters", out _));
    }
}
