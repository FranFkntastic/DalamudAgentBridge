using System.Text.Json;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class AgentBridgeClientTests
{
    [Theory]
    [InlineData("{\"refreshActive\":false}", "refreshActive", "false", true)]
    [InlineData("{\"refresh\":{\"state\":\"Succeeded\"}}", "refresh.state", "succeeded", true)]
    [InlineData("{\"refresh\":{\"state\":\"Running\"}}", "refresh.state", "succeeded", false)]
    [InlineData("{\"refreshActive\":false}", "missing.path", null, false)]
    public void MatchesResolvesDotPathsAndScalarValues(string json, string path, string? expected, bool matches)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(matches, AgentBridgeClient.Matches(document.RootElement, new BridgeWaitCondition(path, expected)));
    }

    [Fact]
    public void MatchesCanWaitForAPathToBeAbsent()
    {
        using var document = JsonDocument.Parse("{\"ready\":true}");

        Assert.True(AgentBridgeClient.Matches(document.RootElement, new BridgeWaitCondition("error", Exists: false)));
    }
}
