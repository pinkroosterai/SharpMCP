# SharpMCP - Project Context

## What is this?

SharpMCP is an MCP server written in C# that provides Roslyn-powered code intelligence tools for LLMs. It lets Claude Code query C# codebase structure without reading entire files.

## Build & Run

```bash
dotnet build src/SharpMCP
dotnet run --project src/SharpMCP
```

## Architecture

Single .NET 9 console app using stdio MCP transport.

```
Tools/ (5 classes)     → Thin MCP tool layer, handles input/output
Services/ (6 classes)  → Business logic using Roslyn APIs
Formatting/ (2 classes)→ Symbol/reference → text conversion
Models/ (5 files)      → Shared DTOs (records)
Program.cs             → Host bootstrap, DI registration
```

Key components:
- **WorkspaceManager**: Loads solutions via MSBuildWorkspace, caches compilations, auto-invalidates on file changes
- **SymbolResolver**: Finds types/symbols by name across all projects
- **SymbolFormatter**: Converts Roslyn ISymbol data to compact text output

## Conventions

- Tools return strings (not JSON) — compact text is more token-efficient for LLM consumption
- All tool methods catch exceptions and return `"Error: ..."` strings
- `detail` parameter: `"compact"` (default) = signatures + locations, `"full"` = includes source bodies and docs
- Services are registered as singletons via DI
- `MSBuildLocator.RegisterDefaults()` MUST be called before any Roslyn types are loaded (first line of Program.cs)

## NuGet Dependencies

- `ModelContextProtocol` 0.8.0-preview.1 (MCP SDK)
- `Microsoft.Extensions.Hosting` 9.x
- `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.0.0 (Roslyn)
- `Microsoft.CodeAnalysis.Workspaces.MSBuild` 5.0.0
- `Microsoft.Build.Locator` 1.11.2
