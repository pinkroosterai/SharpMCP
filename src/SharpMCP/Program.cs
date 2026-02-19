using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using SharpMCP.Formatting;
using SharpMCP.Services;

// MUST register MSBuild before any Roslyn types are loaded
MSBuildLocator.RegisterDefaults();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register services
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<LocationFormatter>();
builder.Services.AddSingleton<SymbolFormatter>();
builder.Services.AddSingleton<SymbolResolver>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<SymbolSearchService>();
builder.Services.AddSingleton<SourceService>();
builder.Services.AddSingleton<HierarchyService>();
builder.Services.AddSingleton<ReferencesService>();
builder.Services.AddSingleton<RenameService>();
builder.Services.AddSingleton<InterfaceService>();
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddSingleton<SignatureService>();

// Configure MCP server
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
