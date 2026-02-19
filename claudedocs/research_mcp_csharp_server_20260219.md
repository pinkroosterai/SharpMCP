# Research Report: Implementing an MCP Server in C# Using the Official SDK

**Date:** 2026-02-19
**Depth:** Deep
**Confidence:** High

---

## Executive Summary

The **Model Context Protocol (MCP) C# SDK** is the official .NET SDK for building MCP servers and clients, developed by Anthropic in collaboration with Microsoft. It is distributed as NuGet packages (currently in preview) and provides a first-class developer experience with dependency injection, attribute-based tool/resource/prompt registration, and support for both stdio and HTTP transports. The SDK is production-ready for building servers that expose tools, resources, and prompts to LLM-powered applications.

---

## 1. What is MCP?

MCP (Model Context Protocol) is an **open protocol** that standardizes how AI applications discover and invoke tools, load contextual data, and execute guided workflows. It uses a **client-server model built on JSON-RPC 2.0**.

### Core Primitives

| Primitive | Purpose | Description |
|-----------|---------|-------------|
| **Tools** | Executable functions | Functions the LLM can invoke (e.g., API calls, computations) |
| **Resources** | Contextual data | Structured data the client can include in its context |
| **Prompts** | Reusable templates | Standardized prompt templates with placeholder parameters |

### Architecture

```
Host Application (e.g., Claude Desktop, VS Code)
    |
    v
MCP Client  <--JSON-RPC 2.0-->  MCP Server
                                    |
                                    v
                              Local/Remote Services, APIs, Databases
```

---

## 2. Official SDK Packages

The C# SDK consists of **three NuGet packages**:

| Package | Purpose | Use When |
|---------|---------|----------|
| `ModelContextProtocol` | Main package with hosting and DI extensions | Most projects (stdio transport) |
| `ModelContextProtocol.AspNetCore` | HTTP-based MCP servers (SSE / Streamable HTTP) | Remote/web-hosted servers |
| `ModelContextProtocol.Core` | Minimal dependencies, client + low-level server APIs | Lightweight scenarios |

### Installation

```bash
# For stdio-based servers (most common)
dotnet add package ModelContextProtocol --prerelease
dotnet add package Microsoft.Extensions.Hosting

# For HTTP-based servers (remote/SSE)
dotnet add package ModelContextProtocol.AspNetCore --prerelease
```

> **Note:** The SDK is in preview. The `--prerelease` flag is required. Breaking changes may occur between versions.

### Latest Version

As of February 2026, the latest version is **0.8.0-preview.1** (main package). The SDK supports protocol version **2025-06-18** which includes OAuth authentication, elicitation support, structured tool output, and resource links.

### Source & Docs

- **GitHub:** [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)
- **API Docs:** [modelcontextprotocol.github.io/csharp-sdk](https://modelcontextprotocol.github.io/csharp-sdk/)
- **NuGet:** [nuget.org/packages/ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol/)

---

## 3. Creating a Basic MCP Server (Stdio Transport)

### Step 1: Project Setup

```bash
dotnet new console -n MyMcpServer
cd MyMcpServer
dotnet add package ModelContextProtocol --prerelease
dotnet add package Microsoft.Extensions.Hosting
```

### Step 2: Program.cs (Server Bootstrap)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (stdout is used for MCP protocol messages)
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();
```

**Key points:**
- `AddMcpServer()` registers MCP server services with the DI container
- `WithStdioServerTransport()` configures communication over stdin/stdout
- `WithToolsFromAssembly()` scans the assembly for `[McpServerToolType]` classes
- `WithResourcesFromAssembly()` scans for `[McpServerResourceType]` classes
- `WithPromptsFromAssembly()` scans for `[McpServerPromptType]` classes
- **All logging MUST go to stderr** to avoid interfering with the JSON-RPC protocol on stdout

### Step 3: Server Metadata (Optional)

```csharp
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "MyMcpServer",
        Version = "1.0.0"
    };
    options.ServerInstructions = "This server provides weather data tools.";
});
```

---

## 4. Implementing Tools

Tools are the most commonly used MCP primitive. They represent executable functions the LLM can call.

### Attribute-Based Approach (Recommended)

```csharp
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"Hello from C#: {message}";

    [McpServerTool, Description("Echoes the message in reverse.")]
    public static string ReverseEcho(string message) =>
        new string(message.Reverse().ToArray());
}
```

### Tools with Dependency Injection

Tool classes can be **non-static** and use constructor injection:

```csharp
[McpServerToolType]
public sealed class WeatherTools
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WeatherTools> _logger;

    public WeatherTools(IHttpClientFactory httpClientFactory, ILogger<WeatherTools> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [McpServerTool(Name = "get_alerts", Title = "Weather Alerts", ReadOnly = true, Idempotent = true)]
    [Description("Get weather alerts for a US state")]
    [McpMeta("category", "weather")]
    public async Task<string> GetAlerts(
        [Description("Two-letter state abbreviation (e.g., NY)")] string state,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching alerts for {State}", state);
        var client = _httpClientFactory.CreateClient("WeatherApi");
        var response = await client.GetFromJsonAsync<AlertResponse>(
            $"/alerts/active/area/{state}", cancellationToken);

        if (response?.Features.Count == 0)
            return "No active alerts";

        return string.Join("\n---\n", response.Features.Select(f =>
            $"Event: {f.Properties.Event}\nSeverity: {f.Properties.Severity}\n" +
            $"Description: {f.Properties.Description}"));
    }
}
```

Register the `HttpClient` in DI:

```csharp
builder.Services.AddHttpClient("WeatherApi", client =>
{
    client.BaseAddress = new Uri("https://api.weather.gov");
    client.DefaultRequestHeaders.UserAgent.Add(
        new ProductInfoHeaderValue("MCPServer", "1.0"));
});
```

### Advanced Tool Features

- **`McpServer` injection:** Tools can receive the `McpServer` instance as a parameter to interact with the client (e.g., sampling requests)
- **`CancellationToken`:** Supported as a parameter for async operations
- **Error handling:** Throw `McpException` for domain errors or `McpProtocolException` for protocol-level errors
- **Tool annotations:** `Name`, `Title`, `ReadOnly`, `Idempotent` on `[McpServerTool]`; `[McpMeta("key", "value")]` for custom metadata
- **XML comments:** If a tool method is marked `partial`, XML comments auto-generate `[Description]` attributes

---

## 5. Implementing Resources

Resources provide contextual data to the client.

### Attribute-Based Resources

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

[McpServerResourceType]
public class HomeAddressResource
{
    public static Coordinates? HomeAddressCoordinates { get; set; }

    [McpServerResource(
        Name = "home_address_coordinates",
        Title = "Home Address Coordinates",
        MimeType = "application/json")]
    [Description("The user's home address coordinates, if set.")]
    public string GetHomeAddress()
    {
        if (HomeAddressCoordinates is null)
            return JsonSerializer.Serialize(new { Error = "Not set" });

        return JsonSerializer.Serialize(HomeAddressCoordinates);
    }
}
```

### Manual Handler Registration (Resources)

```csharp
Handlers = new McpServerHandlers
{
    ListResourcesHandler = (request, cancellationToken) =>
    {
        return ValueTask.FromResult(new ListResourcesResult
        {
            Resources =
            [
                new Resource
                {
                    Uri = "memory://data/config",
                    Name = "Configuration",
                    MimeType = "application/json",
                    Description = "Server configuration"
                }
            ]
        });
    },

    ReadResourceHandler = (request, cancellationToken) =>
    {
        if (request.Params?.Uri == "memory://data/config")
        {
            return ValueTask.FromResult(new ReadResourceResult
            {
                Contents =
                [
                    new TextContent
                    {
                        Uri = request.Params.Uri,
                        Text = JsonSerializer.Serialize(new { setting1 = "value1" }),
                        MimeType = "application/json"
                    }
                ]
            });
        }
        throw new McpProtocolException("Resource not found", McpErrorCode.InvalidRequest);
    }
}
```

### Resource Features

- **Static resources:** Fixed URIs with known content
- **Resource templates:** Dynamic URIs with parameters (e.g., `user://profile/{user_id}`)
- **Subscriptions:** Clients can subscribe to resource updates via `notifications/resources/updated`
- **List changed notifications:** Servers can notify when available resources change

---

## 6. Implementing Prompts

Prompts are reusable templates that guide complex interactions.

### Attribute-Based Prompts

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerPromptType]
public class UpdateHomeAddressPrompt
{
    [McpServerPrompt(Name = "update_home_address_prompt", Title = "Update Home Address")]
    [Description("A prompt to update the user's home address.")]
    public string GetUpdateHomeAddressPrompt(decimal latitude, decimal longitude) =>
        $"Please update the user's home address to latitude {latitude} and longitude {longitude}.";
}
```

### Prompts with DI

```csharp
[McpServerPromptType]
internal sealed class JokePrompt
{
    private readonly ILogger _logger;

    public JokePrompt(ILogger<JokePrompt> logger)
    {
        _logger = logger;
    }

    [McpServerPrompt(Name = "Joke"), Description("Tell a joke about a topic.")]
    public IReadOnlyCollection<ChatMessage> Format(
        [Description("The topic of the joke.")] string topic)
    {
        _logger.LogInformation("Generating prompt with topic: {Topic}", topic);
        var content = $"Tell a joke about {topic}.";
        return [new(ChatRole.User, content)];
    }
}
```

---

## 7. HTTP Transport (Remote Servers)

For remote/web-hosted MCP servers, use `ModelContextProtocol.AspNetCore`.

### Minimal HTTP Server

```csharp
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();  // Maps MCP endpoints (supports both Streamable HTTP and legacy SSE)

app.Run();
```

### Advanced HTTP Configuration

```csharp
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "RemoteServer", Version = "2.0.0" };
})
.WithHttpTransport(httpOptions =>
{
    httpOptions.Stateless = false;  // Enable stateful sessions
    httpOptions.IdleTimeout = TimeSpan.FromMinutes(30);
    httpOptions.OnSessionStart = async (httpContext) =>
    {
        var userId = httpContext.User.Identity?.Name;
        Console.WriteLine($"Session started for user: {userId}");
    };
})
.AddAuthorizationFilters()  // Enable [Authorize] on tools
.WithTools<CalculatorTool>()
.WithPrompts<AnalysisPrompts>()
.WithResources<DataResourceProvider>();

builder.Services.AddAuthorization();
builder.Services.AddAuthentication();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp");  // Custom route prefix

app.Run();
```

### Transport Comparison

| Feature | Stdio | HTTP (Streamable HTTP / SSE) |
|---------|-------|------------------------------|
| **Package** | `ModelContextProtocol` | `ModelContextProtocol.AspNetCore` |
| **Use case** | Local tools, CLI integrations | Remote servers, multi-client |
| **Communication** | stdin/stdout | HTTP POST + SSE streaming |
| **Multiple clients** | No (1:1) | Yes |
| **Deployment** | Run as process | Web server, Azure Functions, containers |
| **Configuration** | `WithStdioServerTransport()` | `WithHttpTransport()` + `MapMcp()` |

### SSE Endpoints (Backward Compatibility)

When using `MapMcp()`, the SDK automatically maps:
- **Streamable HTTP:** Single endpoint (e.g., `/mcp`) for both GET and POST
- **Legacy SSE:** `/sse` (GET to establish connection) and `/message` (POST to send messages)

---

## 8. Registration Approaches

### Approach 1: Assembly Scanning (Simplest)

```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();
```

> **Note:** Assembly scanning uses reflection and is **not compatible with Native AOT**.

### Approach 2: Explicit Registration (AOT-Compatible)

```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<WeatherTools>()
    .WithTools<EchoTool>()
    .WithResources<ConfigResource>()
    .WithPrompts<AnalysisPrompts>();
```

### Approach 3: Manual Handlers (Full Control)

```csharp
var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "CustomServer", Version = "1.0.0" },
    Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability { ListChanged = true },
        Resources = new ResourcesCapability { Subscribe = true, ListChanged = true },
        Prompts = new PromptsCapability { ListChanged = false }
    },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (request, ct) => { /* ... */ },
        CallToolHandler = (request, ct) => { /* ... */ },
        ListResourcesHandler = (request, ct) => { /* ... */ },
        ReadResourceHandler = (request, ct) => { /* ... */ },
    }
};

var transport = new StdioServerTransport("CustomServer");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
```

---

## 9. Configuring Clients to Use Your Server

### VS Code / GitHub Copilot (`.vscode/mcp.json`)

```json
{
    "inputs": [],
    "servers": {
        "MyMcpServer": {
            "type": "stdio",
            "command": "dotnet",
            "args": [
                "run",
                "--project",
                "path/to/MyMcpServer.csproj"
            ]
        }
    }
}
```

### Claude Desktop (`claude_desktop_config.json`)

```json
{
    "mcpServers": {
        "MyMcpServer": {
            "command": "dotnet",
            "args": [
                "run",
                "--project",
                "/absolute/path/to/MyMcpServer.csproj"
            ]
        }
    }
}
```

### HTTP Server Configuration

```json
{
    "servers": {
        "MyRemoteServer": {
            "type": "http",
            "url": "http://localhost:5000/mcp"
        }
    }
}
```

---

## 10. Testing

### MCP Inspector

The official testing tool for MCP servers:

```bash
npx @modelcontextprotocol/inspector dotnet run --project path/to/MyMcpServer.csproj
```

This launches a visual UI for:
- Listing available tools, resources, and prompts
- Invoking tools with parameters
- Reading resources
- Testing prompt templates

### Debugging in VS Code

Add to `.vscode/launch.json`:

```json
{
    "configurations": [
        {
            "name": ".NET Core Attach",
            "type": "coreclr",
            "request": "attach",
            "processName": "MyMcpServer.exe"
        }
    ]
}
```

Start the server via the MCP client, then attach the debugger.

---

## 11. Publishing & Deployment

### NuGet Distribution

MCP servers can be published as NuGet packages and executed via `dnx` (available in .NET 10+):

```bash
dnx MyMcpServer@1.0.0 --yes
```

### Native AOT Compilation

The SDK is **AOT-compatible**. Publish as a self-contained native binary:

```bash
dotnet publish -c Release -r linux-x64 --self-contained /p:PublishAot=true
```

> **Requirement:** Use explicit tool registration (`WithTools<T>()`) instead of assembly scanning when using AOT.

### Docker / Azure Container Apps

For remote servers, containerize the ASP.NET Core app and deploy to any container host.

---

## 12. Protocol Version 2025-06-18 Features

The latest protocol version adds:

| Feature | Description |
|---------|-------------|
| **OAuth 2.0 Authentication** | Separated Authorization Server / Resource Server roles |
| **Elicitation** | Server can ask users for additional input during tool execution |
| **Structured Tool Output** | Tools produce structured JSON output based on schemas |
| **Resource Links in Tool Results** | Tools can return links to resources |
| **Titles & Metadata** | `title` field + `_meta` on tools, resources, prompts |
| **MCP-Protocol-Version Header** | Required HTTP header for protocol version negotiation |

---

## 13. Best Practices & Recommendations

1. **Logging to stderr:** Always configure console logging to write to stderr, not stdout
2. **Descriptions everywhere:** Use `[Description]` on tool classes, methods, and parameters -- LLMs use these to understand when/how to invoke tools
3. **Use DI:** Leverage .NET's dependency injection for services, HttpClient, logging
4. **Error handling:** Throw `McpException` for tool-level errors; use proper error codes
5. **CancellationToken:** Always accept and propagate cancellation tokens in async tools
6. **Tool naming:** Use `snake_case` for tool names (e.g., `get_weather_alerts`)
7. **Prefer explicit registration** over assembly scanning for production/AOT scenarios
8. **Keep tools focused:** Each tool should do one thing well; descriptions guide the LLM
9. **Test with MCP Inspector** before integrating with LLM clients
10. **Pin SDK version** in production to avoid breaking changes from preview updates

---

## 14. Complete Minimal Example

```csharp
// Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "DemoServer", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

// Tools
[McpServerToolType]
public static class DemoTools
{
    [McpServerTool, Description("Adds two numbers together.")]
    public static double Add(
        [Description("First number")] double a,
        [Description("Second number")] double b) => a + b;

    [McpServerTool, Description("Gets the current UTC date and time.")]
    public static string GetCurrentTime() => DateTime.UtcNow.ToString("O");
}
```

---

## Sources

- [Official C# SDK GitHub Repository](https://github.com/modelcontextprotocol/csharp-sdk)
- [Official SDK Documentation](https://modelcontextprotocol.github.io/csharp-sdk/)
- [NuGet Package: ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol/)
- [Microsoft .NET Blog: Build an MCP Server in C#](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/)
- [Microsoft Developer Blog: C# SDK Partnership](https://developer.microsoft.com/blog/microsoft-partners-with-anthropic-to-create-official-c-sdk-for-model-context-protocol)
- [.NET Blog: SDK Update for Protocol 2025-06-18](https://devblogs.microsoft.com/dotnet/mcp-csharp-sdk-2025-06-18-update/)
- [Microsoft Learn: Quickstart MCP Server](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server)
- [Dometrain: Build and Consume MCP Servers in .NET](https://dometrain.com/blog/how-to-build-and-consume-mcp-servers-in-dotnet/)
- [mcpindotnet Tutorial: Build Time MCP Server](https://mcpindotnet.github.io/docs/tutorials/build-time-mcp-server/)
- [MCP Protocol Specification](https://modelcontextprotocol.io)
