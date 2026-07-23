using System.Runtime.CompilerServices;
using ModelContextProtocol.Client;
using Xunit;

namespace DalamudAgentBridge.Tests;

public sealed class McpServerIntegrationTests
{
    [Fact]
    public async Task StdioServerAdvertisesAndRunsCanonicalTools()
    {
        var repository = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", ".."));
        var server = new[]
        {
            Path.Combine(repository, "src", "dab-mcp", "bin", "Debug", "net8.0-windows10.0.26100.0", "win-x64", "dab-mcp.exe"),
            Path.Combine(repository, "src", "dab-mcp", "bin", "x64", "Debug", "net8.0-windows10.0.26100.0", "win-x64", "dab-mcp.exe"),
        }.FirstOrDefault(File.Exists) ?? Path.Combine(repository, "src", "dab-mcp", "bin", "Debug", "net8.0-windows10.0.26100.0", "win-x64", "dab-mcp.exe");
        Assert.True(File.Exists(server), $"Build the solution before running the MCP integration test: {server}");
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Dalamud Agent Bridge integration test",
            Command = server,
            InheritEnvironmentVariables = true,
        }));

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "bridge_list");
        Assert.Contains(tools, tool => tool.Name == "bridge_act");
        Assert.Contains(tools, tool => tool.Name == "bridge_deploy");
        Assert.Contains(tools, tool => tool.Name == "bridge_capture");
        var result = await client.CallToolAsync("bridge_list", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        Assert.NotEmpty(result.Content);
    }

    private static string SourceDirectory([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Test source path was unavailable.");
}
