# SharpMCP

An MCP (Model Context Protocol) server that uses the Roslyn compiler platform to provide LLMs with efficient, surgical access to C# codebase intelligence.

Instead of loading entire source files into context, LLMs can issue targeted queries — "what implements this interface?", "where is this method called?", "what's the signature of this class?" — and receive compact, structured answers.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (or later)

## Build

```bash
dotnet build src/SharpMCP
```

## Configuration

### Claude Code

Add to your Claude Code MCP settings (`~/.claude/claude_desktop_config.json` or project-level):

```json
{
  "mcpServers": {
    "sharpmcp": {
      "command": "dotnet",
      "args": ["run", "--project", "/absolute/path/to/SharpMCP/src/SharpMCP"]
    }
  }
}
```

Or after publishing:

```json
{
  "mcpServers": {
    "sharpmcp": {
      "command": "/path/to/published/SharpMCP"
    }
  }
}
```

## Tools (20)

### Project Structure (4 tools)

| Tool | Description |
|------|-------------|
| `list_projects` | List all projects in a solution with metadata |
| `get_project_info` | Get detailed project metadata including references and packages |
| `list_source_files` | List all .cs files in a project |
| `get_diagnostics` | Get compilation errors and warnings |

### Symbol Search (4 tools)

| Tool | Description |
|------|-------------|
| `find_symbol` | Search by name (substring or exact match), filter by kind |
| `get_file_symbols` | List all symbols in a file (depth=1 for members) |
| `get_type_members` | List members of a type (methods, properties, etc.) |
| `list_namespaces` | List all namespaces in a solution |

### Type Hierarchy (3 tools)

| Tool | Description |
|------|-------------|
| `find_derived_types` | Find implementations (interfaces) or subclasses (classes) |
| `get_type_hierarchy` | Full inheritance chain (bases + interfaces) |
| `find_overrides` | Find overrides of a virtual/abstract method |

### References (1 tool)

| Tool | Description |
|------|-------------|
| `find_references` | Find references, callers, or usages (via mode parameter) |

### Source Retrieval (2 tools)

| Tool | Description |
|------|-------------|
| `get_source` | Get source code for a specific symbol |
| `get_file_content` | Get file content with line numbers |

### Refactoring (4 tools)

| Tool | Description |
|------|-------------|
| `rename_symbol` | Rename a symbol and update all references |
| `extract_interface` | Generate an interface from a class's public members |
| `implement_interface` | Add stub implementations for interface members |
| `change_signature` | Modify method parameters and update call sites |

### Analysis (2 tools)

| Tool | Description |
|------|-------------|
| `find_unused_code` | Find unreferenced symbols across the solution |
| `find_code_smells` | Detect code smells (complexity, design, inheritance) grouped by severity |

## Common Parameters

- **`solutionPath`**: Path to `.sln` or `.csproj` file. The server caches loaded solutions internally.
- **`detail`**: `"compact"` (default) or `"full"`. Compact returns signatures + locations. Full includes source, docs, and context.

## How It Works

SharpMCP uses the [Roslyn](https://github.com/dotnet/roslyn) compiler platform (`Microsoft.CodeAnalysis`) to:

1. Load solutions/projects via `MSBuildWorkspace`
2. Build full `Compilation` objects with semantic analysis
3. Use `SymbolFinder` APIs for implementations, references, and call sites
4. Cache loaded solutions to avoid reloading on every query
5. Automatically detect when source files change and invalidate the cache

## Troubleshooting

| Problem | Solution |
|---------|----------|
| "Solution not found" | Verify the path is absolute and the file exists |
| "Run 'dotnet restore' first" | Run `dotnet restore` on the target solution |
| "MSBuild SDK not found" | Install the .NET SDK matching the target project |
| Slow first query | Expected — first load builds the full compilation. Subsequent queries use cache |
| "Type 'X' not found" | Check for typos; use `find_symbol` to search by substring |

## License

See [LICENSE](LICENSE).
