using DalamudAgentBridge;
using DalamudAgentBridge.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<BridgeRegistry>();
builder.Services.AddSingleton<NamedPipeBridgeClient>();
builder.Services.AddSingleton<ReviewedControlPresentationService>();
builder.Services.AddSingleton<DalamudLogWatcher>();
builder.Services.AddSingleton<AgentBridgeClient>();
builder.Services.AddSingleton<DevPluginDeploymentService>();
builder.Services.AddSingleton<ReviewVault>();
builder.Services.AddSingleton<PluginCaptureService>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AgentBridgeTools>();

await builder.Build().RunAsync();
