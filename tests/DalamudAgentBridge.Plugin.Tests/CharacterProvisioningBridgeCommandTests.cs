using DalamudAgentBridge.Plugin;
using Franthropy.Dalamud.AgentBridge;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class CharacterProvisioningBridgeCommandTests
{
    [Fact]
    public async Task AuthenticatedCommandSerializesSelectionAndKeepsFailuresClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "DalamudAgentBridge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pipeName = $"DalamudAgentBridge.Tests.{Guid.NewGuid():N}";
            var protectedToken = string.Empty;
            var snapshotCalls = 0;
            var frameworkCalls = 0;
            var mutationCalled = false;
            CharacterProvisioningSelectionObservation selection = Resolve([new("Adamantoise", "Adamantoise", "FakeAddon.SelectedItemIndex")]);
            object CreateSnapshot()
            {
                snapshotCalls++;
                return new { schemaVersion = 1, selection };
            }

            var router = new AgentBridgeCommandRouter();
            new CharacterProvisioningBridgeCommand(
                CreateSnapshot,
                action =>
                {
                    frameworkCalls++;
                    return Task.FromResult(action());
                }).Register(router);
            var identity = AgentBridgeRuntimeIdentity.FromAssembly("DalamudAgentBridge.Tests", typeof(CharacterProvisioningBridgeCommandTests).Assembly);
            var manifest = new AgentBridgeManifest(2, identity, "test", "test", "test.v1", [], [], [], []);
            using var host = new Franthropy.Dalamud.AgentBridge.AgentBridgeHost(new AgentBridgeHostOptions
            {
                ConfigDirectory = root,
                PluginInstanceId = "character-provisioning-test",
                PipeName = pipeName,
                GetProtectedAccessToken = () => protectedToken,
                SetProtectedAccessToken = value => protectedToken = value,
                SaveConfiguration = () => { },
                CreateManifest = () => manifest,
                HandleRequestAsync = router.HandleAsync,
            });
            host.Start();
            var token = AgentBridgeDataProtection.UnprotectToken(protectedToken, "character-provisioning-test");

            var ok = await SendAsync(pipeName, token);
            Assert.True(ok.Success);
            AssertSelection(ok.Receipt, "ok", "Adamantoise", "Adamantoise", "FakeAddon.SelectedItemIndex");

            selection = Resolve([]);
            var unknown = await SendAsync(pipeName, token);
            Assert.True(unknown.Success);
            AssertSelection(unknown.Receipt, "unknown", null, null, "AddonSelectionState");

            selection = Resolve([
                new("Adamantoise", "Adamantoise", "FakeAddon.ListA.SelectedItemIndex"),
                new("Adamantoise", "Adamantoise", "FakeAddon.ListB.SelectedItemIndex"),
            ]);
            var ambiguous = await SendAsync(pipeName, token);
            Assert.True(ambiguous.Success);
            AssertSelection(ambiguous.Receipt, "ambiguous", null, null, "AddonSelectionState");

            Assert.Equal(3, snapshotCalls);
            Assert.Equal(3, frameworkCalls);
            Assert.False(mutationCalled);
            Assert.DoesNotContain(
                typeof(CharacterProvisioningBridgeCommand).GetConstructors().Single().GetParameters(),
                parameter => parameter.ParameterType == typeof(Action));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CharacterProvisioningSelectionObservation Resolve(CharacterProvisioningSelectionCandidate[] candidates) =>
        CharacterProvisioningSelectionResolver.Resolve(candidates);

    private static async Task<ResponseEnvelope> SendAsync(string pipeName, string token)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5_000);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentBridgeRequest
        {
            Token = token,
            Command = "get-character-provisioning",
        }, WebJson));
        return JsonSerializer.Deserialize<ResponseEnvelope>((await reader.ReadLineAsync())!, WebJson)!;
    }

    private static void AssertSelection(JsonElement receipt, string status, string? world, string? choice, string source)
    {
        var selection = receipt.GetProperty("selection");
        Assert.Equal(1, selection.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(status, selection.GetProperty("status").GetString());
        Assert.Equal(world, selection.GetProperty("selectedWorld").GetString());
        Assert.Equal(choice, selection.GetProperty("selectedChoice").GetString());
        Assert.Equal(source, selection.GetProperty("source").GetString());
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private sealed record ResponseEnvelope(bool Success, string Message, JsonElement Receipt);
}
