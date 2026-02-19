# Architecture Design: SharpMCP

**Date:** 2026-02-19
**Status:** Draft - Pending Approval

---

## 1. System Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                        Claude Code (CLI)                         │
│                    (MCP Client via stdio)                         │
└──────────────────────┬───────────────────────────────────────────┘
                       │ JSON-RPC 2.0 over stdin/stdout
                       ▼
┌──────────────────────────────────────────────────────────────────┐
│                     SharpMCP Server Process                       │
│                                                                   │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │                    MCP Tool Layer                            │ │
│  │  ProjectTools  SymbolTools  HierarchyTools  ReferenceTools   │ │
│  │  SourceTools                                                 │ │
│  └──────────────────────┬──────────────────────────────────────┘ │
│                         │ delegates to                            │
│  ┌──────────────────────▼──────────────────────────────────────┐ │
│  │                   Service Layer                              │ │
│  │  WorkspaceManager   SymbolSearchService   HierarchyService   │ │
│  │  ReferencesService  ProjectService        SourceService      │ │
│  └──────────────────────┬──────────────────────────────────────┘ │
│                         │ uses                                    │
│  ┌──────────────────────▼──────────────────────────────────────┐ │
│  │                 Infrastructure Layer                         │ │
│  │  SymbolFormatter   CompilationCache   DiagnosticsHelper      │ │
│  └──────────────────────┬──────────────────────────────────────┘ │
│                         │ wraps                                   │
│  ┌──────────────────────▼──────────────────────────────────────┐ │
│  │              Roslyn APIs (Microsoft.CodeAnalysis)            │ │
│  │  MSBuildWorkspace  Compilation  SemanticModel  ISymbol       │ │
│  │  SyntaxTree  SyntaxNode  FindReferences  SymbolFinder        │ │
│  └─────────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────┘
```

---

## 2. Project Structure

Single .NET 9 console application. No multi-project solution needed for v1.

```
SharpMCP/
├── SharpMCP.csproj
├── Program.cs                          # Host setup, DI, MCP server config
│
├── Tools/                              # MCP Tool classes (thin delegation layer)
│   ├── ProjectTools.cs                 # list_projects, get_project_info, etc.
│   ├── SymbolTools.cs                  # find_symbol, get_file_symbols, etc.
│   ├── HierarchyTools.cs              # find_implementations, find_subclasses, etc.
│   ├── ReferenceTools.cs              # find_references, find_callers, etc.
│   └── SourceTools.cs                 # get_source, get_file_content
│
├── Services/                           # Business logic layer
│   ├── WorkspaceManager.cs            # Solution loading, caching, invalidation
│   ├── SymbolSearchService.cs         # Symbol search & overview queries
│   ├── HierarchyService.cs           # Type hierarchy & implementation queries
│   ├── ReferencesService.cs           # Find references / call sites
│   ├── ProjectService.cs             # Project structure & dependency queries
│   └── SourceService.cs              # Source code retrieval
│
├── Formatting/                         # Output formatting for LLM consumption
│   ├── SymbolFormatter.cs             # ISymbol → compact/full string
│   ├── LocationFormatter.cs           # Location → "file.cs:42" with snippet
│   └── DetailLevel.cs                 # Enum: Compact | Full
│
├── Models/                             # Shared data transfer objects
│   ├── SymbolResult.cs
│   ├── ReferenceResult.cs
│   ├── ProjectInfo.cs
│   └── TypeHierarchyResult.cs
│
└── claudedocs/                         # Research & design docs (not shipped)
    ├── research_mcp_csharp_server_20260219.md
    ├── research_roslyn_codeanalysis_csharp_20260219.md
    ├── requirements_sharpmcp_20260219.md
    └── design_sharpmcp_20260219.md
```

---

## 3. NuGet Dependencies

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- MCP Server SDK -->
    <PackageReference Include="ModelContextProtocol" Version="0.8.0-preview.1" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />

    <!-- Roslyn APIs -->
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.0.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.0.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.0.0" />
    <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
  </ItemGroup>
</Project>
```

> **Note:** Using Roslyn 5.0.0 (ships with .NET 9 SDK) and Microsoft.Extensions.Hosting 9.0.0 to match the .NET 9 target framework.

---

## 4. Component Design

### 4.1 Program.cs — Server Bootstrap

```
Responsibilities:
  - Register MSBuildLocator (MUST be first, before any Roslyn types load)
  - Configure Microsoft.Extensions.Hosting
  - Register MCP server with stdio transport
  - Register all services in DI container
  - Configure logging to stderr
  - Scan assembly for tool classes

Data flow:
  MSBuildLocator.RegisterDefaults()
    → Host.CreateApplicationBuilder()
    → builder.Services.AddSingleton<WorkspaceManager>()
    → builder.Services.AddSingleton<SymbolFormatter>()
    → builder.Services.Add...Services()
    → builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()
    → builder.Build().RunAsync()
```

### 4.2 WorkspaceManager — Central Cache & Workspace Provider

This is the most critical component. All tools depend on it.

```
Class: WorkspaceManager (Singleton)

Responsibilities:
  - Load solutions/projects via MSBuildWorkspace
  - Cache Compilation objects per solution path
  - Invalidate cache when source files change
  - Provide Compilation + SemanticModel to services
  - Report clear errors on load failure

State:
  - Dictionary<string, CachedWorkspace> _cache
    where CachedWorkspace = {
      MSBuildWorkspace Workspace,
      Solution Solution,
      Dictionary<string, Compilation> Compilations,  // keyed by project name
      DateTime LoadedAt,
      string NormalizedPath
    }

Key Methods:
  + Task<Solution> GetSolutionAsync(string solutionOrProjectPath)
  + Task<Compilation> GetCompilationAsync(string solutionPath, string? projectName = null)
  + Task<SemanticModel> GetSemanticModelAsync(string solutionPath, string filePath)
  + Task<Project> GetProjectAsync(string solutionPath, string projectName)
  + void InvalidateCache(string solutionPath)

Cache Invalidation Strategy (v1 - simple):
  - On each GetSolutionAsync call, check if any .cs file in the solution dir
    has a LastWriteTime newer than LoadedAt
  - If so, reload the entire solution
  - For v1 this is sufficient; file watching can be added later

Error Handling:
  - Solution not found → "Solution file not found: {path}"
  - MSBuild not found → "MSBuild SDK not found. Ensure .NET SDK is installed."
  - Restore needed → "Project has unresolved references. Run 'dotnet restore' first."
  - Compilation errors → still return Compilation (tools can query despite errors)
```

### 4.3 Tool Layer — MCP Tool Classes

Each tool class is thin: validate input → call service → format output → return string.

```
Pattern for all tool classes:

  [McpServerToolType]
  public class XxxTools(XxxService service, SymbolFormatter formatter)
  {
      [McpServerTool(Name = "tool_name"), Description("...")]
      public async Task<string> ToolMethod(
          [Description("Path to .sln or .csproj file")] string solutionPath,
          [Description("...")] string param,
          [Description("'compact' or 'full'")] string detail = "compact")
      {
          var detailLevel = DetailLevel.Parse(detail);
          var result = await service.QueryAsync(solutionPath, param);
          return formatter.Format(result, detailLevel);
      }
  }

Error convention:
  - Tools do NOT throw exceptions
  - Errors are returned as descriptive text strings
  - Wrap service calls in try/catch, return "Error: {message}"
```

#### Tool Class Inventory

**ProjectTools** — 6 tools
```
[McpServerToolType]
public class ProjectTools(ProjectService projectService, SymbolFormatter formatter)

Tools:
  list_projects(solutionPath) → "Projects in solution:\n  - MyApp (net8.0, 42 files)\n  - MyApp.Tests (net8.0, 18 files)"
  get_project_info(solutionPath, projectName) → detailed project metadata
  list_project_references(solutionPath, projectName) → "MyApp.Core → MyApp.Data, MyApp.Shared"
  list_package_references(solutionPath, projectName) → "Newtonsoft.Json 13.0.3, Serilog 4.0.0"
  list_source_files(solutionPath, projectName) → file paths list
  get_diagnostics(solutionPath, projectName?) → compilation errors/warnings
```

**SymbolTools** — 5 tools
```
[McpServerToolType]
public class SymbolTools(SymbolSearchService symbolService, SymbolFormatter formatter)

Tools:
  find_symbol(solutionPath, query, kind?, detail?) → matching symbols with locations
  get_file_symbols(filePath, solutionPath, depth?, detail?) → symbol tree for a file
  get_type_members(solutionPath, typeName, detail?) → members of a type
  get_symbol_info(solutionPath, symbolName, detail?) → detailed symbol info
  list_namespaces(solutionPath) → all namespaces
```

**HierarchyTools** — 4 tools
```
[McpServerToolType]
public class HierarchyTools(HierarchyService hierarchyService, SymbolFormatter formatter)

Tools:
  find_implementations(solutionPath, interfaceName, detail?) → implementing classes
  find_subclasses(solutionPath, baseClassName, detail?) → derived classes
  get_type_hierarchy(solutionPath, typeName) → full inheritance chain
  find_overrides(solutionPath, typeName, methodName, detail?) → override locations
```

**ReferenceTools** — 3 tools
```
[McpServerToolType]
public class ReferenceTools(ReferencesService referencesService, SymbolFormatter formatter)

Tools:
  find_references(solutionPath, symbolName, typeName?, projectScope?, detail?) → all refs
  find_callers(solutionPath, methodName, typeName?, detail?) → call sites
  find_usages(solutionPath, typeName, detail?) → type usage locations
```

**SourceTools** — 2 tools
```
[McpServerToolType]
public class SourceTools(SourceService sourceService)

Tools:
  get_source(solutionPath, symbolName, typeName?) → source code of a symbol
  get_file_content(filePath, startLine?, endLine?) → raw file content (or range)
```

### 4.4 Service Layer

Each service receives `WorkspaceManager` via DI and implements the actual Roslyn logic.

#### SymbolSearchService

```
Class: SymbolSearchService(WorkspaceManager workspaceManager)

Key Methods:
  + Task<List<SymbolResult>> FindSymbolsAsync(string solutionPath, string query,
      SymbolKindFilter? kind = null)
    Implementation:
      1. compilation = await workspaceManager.GetCompilationAsync(solutionPath)
      2. For each SyntaxTree in compilation:
         - Walk DescendantNodes, filter by name match (Contains/Regex) and kind
         - Use SemanticModel.GetDeclaredSymbol() for each match
         - Build SymbolResult with name, kind, file, line, signature
      3. Return list sorted by file path, then line number

  + Task<List<SymbolResult>> GetFileSymbolsAsync(string solutionPath, string filePath,
      int depth = 0)
    Implementation:
      1. Find the Document matching filePath
      2. Get syntax root, walk top-level members
      3. If depth > 0, include nested members (methods in classes, etc.)
      4. Return symbol list

  + Task<List<SymbolResult>> GetTypeMembersAsync(string solutionPath, string typeName)
    Implementation:
      1. Find the INamedTypeSymbol via compilation.GetSymbolsWithName()
      2. Iterate .GetMembers() — return methods, properties, fields, events
      3. Build SymbolResult for each

  + Task<SymbolResult?> GetSymbolInfoAsync(string solutionPath, string symbolName)
    Implementation:
      1. Find symbol(s) matching name
      2. Return full detail: signature, doc comment, attributes, location
      3. If ambiguous, return all matches with disambiguation info

  + Task<List<string>> ListNamespacesAsync(string solutionPath)
    Implementation:
      1. Walk all type symbols in compilation
      2. Collect distinct ContainingNamespace values
      3. Return sorted list
```

#### HierarchyService

```
Class: HierarchyService(WorkspaceManager workspaceManager)

Key Methods:
  + Task<List<SymbolResult>> FindImplementationsAsync(string solutionPath,
      string interfaceName)
    Implementation:
      1. Get Compilation, find INamedTypeSymbol for the interface
      2. Use Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindImplementationsAsync()
         — this is a built-in Roslyn API that does exactly this
      3. Convert results to SymbolResult list

  + Task<List<SymbolResult>> FindSubclassesAsync(string solutionPath,
      string baseClassName)
    Implementation:
      1. Get the base class INamedTypeSymbol
      2. Use SymbolFinder.FindDerivedClassesAsync()
      3. Convert results

  + Task<TypeHierarchyResult> GetTypeHierarchyAsync(string solutionPath,
      string typeName)
    Implementation:
      1. Find the INamedTypeSymbol
      2. Walk .BaseType chain upward
      3. Collect .AllInterfaces
      4. Return structured hierarchy

  + Task<List<SymbolResult>> FindOverridesAsync(string solutionPath,
      string typeName, string methodName)
    Implementation:
      1. Find the method symbol in the base type
      2. Use SymbolFinder.FindOverridesAsync()
      3. Convert results
```

#### ReferencesService

```
Class: ReferencesService(WorkspaceManager workspaceManager)

Key Methods:
  + Task<List<ReferenceResult>> FindReferencesAsync(string solutionPath,
      string symbolName, string? typeName, string? projectScope)
    Implementation:
      1. Find the ISymbol (disambiguate with typeName if provided)
      2. Get Solution from WorkspaceManager
      3. Use SymbolFinder.FindReferencesAsync(symbol, solution)
      4. Filter by projectScope if provided
      5. For each ReferencedSymbol.Locations:
         - Get file path, line, column
         - Extract surrounding code snippet (±2 lines)
      6. Return as ReferenceResult list

  + Task<List<ReferenceResult>> FindCallersAsync(string solutionPath,
      string methodName, string? typeName)
    Implementation:
      1. Use SymbolFinder.FindCallersAsync(methodSymbol, solution)
      2. Format each caller with location + snippet

  + Task<List<ReferenceResult>> FindUsagesAsync(string solutionPath,
      string typeName)
    Implementation:
      1. Find all references to the type symbol
      2. Categorize by usage kind (parameter, field, variable, return type, etc.)
      3. Return with context
```

#### ProjectService

```
Class: ProjectService(WorkspaceManager workspaceManager)

Key Methods:
  + Task<List<ProjectInfo>> ListProjectsAsync(string solutionPath)
  + Task<ProjectInfo> GetProjectInfoAsync(string solutionPath, string projectName)
  + Task<List<string>> ListProjectReferencesAsync(string solutionPath, string projectName)
  + Task<List<PackageInfo>> ListPackageReferencesAsync(string solutionPath, string projectName)
  + Task<List<string>> ListSourceFilesAsync(string solutionPath, string projectName)
  + Task<List<DiagnosticInfo>> GetDiagnosticsAsync(string solutionPath, string? projectName)

Notes:
  - Package references come from Project.MetadataReferences or by parsing the .csproj XML
  - Project references come from Project.ProjectReferences
  - Diagnostics come from Compilation.GetDiagnostics()
```

#### SourceService

```
Class: SourceService(WorkspaceManager workspaceManager)

Key Methods:
  + Task<string> GetSymbolSourceAsync(string solutionPath, string symbolName,
      string? typeName)
    Implementation:
      1. Find the symbol
      2. Get its declaring SyntaxReference
      3. Get the SyntaxNode → .ToFullString()
      4. Return source code

  + Task<string> GetFileContentAsync(string filePath, int? startLine, int? endLine)
    Implementation:
      1. Read file from disk (no Roslyn needed)
      2. If startLine/endLine provided, return that range
      3. Add line numbers to output
```

### 4.5 Formatting Layer

#### SymbolFormatter

```
Class: SymbolFormatter (Singleton)

Responsibilities:
  Convert Roslyn objects to LLM-friendly text output.

Key Methods:
  + string FormatSymbol(ISymbol symbol, DetailLevel detail)
    Compact: "public class OrderService : IOrderService  [src/Services/OrderService.cs:15]"
    Full:    includes XML doc, attributes, all modifiers, full signature

  + string FormatSymbolList(IEnumerable<SymbolResult> symbols, DetailLevel detail)
    Compact: one line per symbol
    Full:    multi-line with docs

  + string FormatReference(ReferenceResult reference, DetailLevel detail)
    Compact: "src/Controllers/OrderController.cs:42 - var svc = new OrderService();"
    Full:    includes ±2 lines of context around the reference

  + string FormatTypeHierarchy(TypeHierarchyResult hierarchy)
    "OrderService
       ├─ bases: BaseService → object
       └─ implements: IOrderService, IDisposable"

  + string FormatProjectList(IEnumerable<ProjectInfo> projects)
    "Solution: MySolution.sln (3 projects)
       MyApp           net8.0  Exe     42 files
       MyApp.Core      net8.0  Library 28 files
       MyApp.Tests     net8.0  Library 15 files"
```

#### DetailLevel

```
Enum + Helper:

public enum DetailLevel { Compact, Full }

public static class DetailLevelExtensions
{
    public static DetailLevel Parse(string? value) =>
        value?.ToLowerInvariant() == "full" ? DetailLevel.Full : DetailLevel.Compact;
}
```

### 4.6 Models

```csharp
// Shared result types used between services and formatters

public record SymbolResult(
    string Name,
    string FullyQualifiedName,
    string Kind,              // "class", "interface", "method", "property", etc.
    string Signature,         // "public async Task<Order> GetOrderAsync(int id)"
    string FilePath,
    int Line,
    string? DocComment,       // XML doc summary (null in compact mode)
    string? SourceBody        // Full source (null in compact mode)
);

public record ReferenceResult(
    string FilePath,
    int Line,
    int Column,
    string CodeSnippet,       // The line containing the reference
    string? ContextBefore,    // ±2 lines before (null in compact mode)
    string? ContextAfter,     // ±2 lines after (null in compact mode)
    string ContainingSymbol   // "OrderController.CreateOrder"
);

public record ProjectInfo(
    string Name,
    string FilePath,
    string TargetFramework,
    string OutputType,        // "Exe", "Library"
    int SourceFileCount,
    List<string> ProjectReferences
);

public record PackageInfo(string Name, string Version);

public record DiagnosticInfo(
    string Id,                // "CS0246"
    string Severity,          // "Error", "Warning"
    string Message,
    string FilePath,
    int Line
);

public record TypeHierarchyResult(
    string TypeName,
    string Kind,
    List<string> BaseTypes,       // Ordered from immediate base to object
    List<string> Interfaces,
    List<SymbolResult> Members    // Optional, only if requested
);
```

---

## 5. Data Flow Examples

### Example 1: find_implementations("IOrderService")

```
1. Claude Code sends JSON-RPC:
   {"method": "tools/call", "params": {"name": "find_implementations",
    "arguments": {"solutionPath": "/code/MyApp.sln", "interfaceName": "IOrderService"}}}

2. MCP SDK routes to HierarchyTools.FindImplementations()

3. HierarchyTools delegates to HierarchyService.FindImplementationsAsync()

4. HierarchyService calls WorkspaceManager.GetSolutionAsync("/code/MyApp.sln")
   → Cache HIT (already loaded) → returns cached Solution + Compilations

5. HierarchyService finds INamedTypeSymbol for "IOrderService":
   - Iterates compilations, calls compilation.GetSymbolsWithName("IOrderService")
   - Filters for TypeKind.Interface

6. HierarchyService calls SymbolFinder.FindImplementationsAsync(interfaceSymbol, solution)
   → Returns [OrderService, MockOrderService]

7. For each implementation, build SymbolResult with name, file, line, signature

8. HierarchyTools calls formatter.FormatSymbolList(results, DetailLevel.Compact)
   → "Classes implementing IOrderService:\n
       public class OrderService : IOrderService  [src/Services/OrderService.cs:15]\n
       public class MockOrderService : IOrderService  [tests/Mocks/MockOrderService.cs:8]"

9. Return as tool result text
```

### Example 2: find_callers("ProcessPayment")

```
1. ReferenceTools.FindCallers(solutionPath, "ProcessPayment")

2. ReferencesService resolves the method symbol:
   - Gets compilation, searches for method named "ProcessPayment"
   - If multiple matches (overloads), returns all callers for all overloads
   - If typeName provided, disambiguates to specific class

3. SymbolFinder.FindCallersAsync(methodSymbol, solution)
   → Returns callers with locations

4. For each caller location:
   - Get file path + line number
   - Read ±2 lines of surrounding source as context snippet

5. Format as ReferenceResult list

6. Compact output:
   "Callers of ProcessPayment (3 found):
     src/Controllers/PaymentController.cs:67 - await _service.ProcessPayment(order);
     src/Services/OrderService.cs:142 - ProcessPayment(newOrder);
     tests/PaymentTests.cs:38 - sut.ProcessPayment(testOrder);"
```

---

## 6. Error Handling Strategy

```
Error Category          | Detection                      | User-Facing Message
------------------------|--------------------------------|---------------------------------------------
Solution not found      | File.Exists check              | "Solution not found: {path}"
Project not in solution | Solution.Projects lookup       | "Project '{name}' not found in {solutionPath}. Available: A, B, C"
NuGet restore needed    | WorkspaceFailed event          | "Failed to load project. Try running 'dotnet restore {path}' first."
Symbol not found        | GetSymbolsWithName returns []  | "No symbol named '{name}' found. Did you mean: {suggestions}?"
Ambiguous symbol        | Multiple matches               | "Multiple matches for '{name}':\n  1. MyApp.OrderService.Process\n  2. MyApp.PaymentService.Process\nSpecify typeName to disambiguate."
File not found          | File.Exists check              | "File not found: {path}"
MSBuild not found       | MSBuildLocator throws          | "MSBuild SDK not found. Ensure .NET SDK is installed."
General Roslyn error    | try/catch                      | "Analysis error: {exception.Message}"
```

---

## 7. Build Sequence

### Phase 1: Foundation (Implements: FR-1, NFR-1, NFR-6, NFR-7)
1. Create project with NuGet dependencies
2. Implement `Program.cs` with MCP server bootstrap
3. Implement `WorkspaceManager` with caching
4. Implement `SymbolFormatter` (compact mode)
5. Implement `DetailLevel` enum
6. **Test:** Server starts, loads a solution, responds to a simple tool call

### Phase 2: Project Tools (Implements: FR-5)
7. Implement `ProjectService`
8. Implement `ProjectTools` (all 6 tools)
9. **Test:** list_projects, get_project_info work against a real solution

### Phase 3: Symbol Tools (Implements: FR-2, FR-6)
10. Implement `SymbolSearchService`
11. Implement `SourceService`
12. Implement `SymbolTools` (all 5 tools)
13. Implement `SourceTools` (both tools)
14. Implement `SymbolFormatter` full mode
15. **Test:** find_symbol, get_type_members, get_source work

### Phase 4: Hierarchy Tools (Implements: FR-3)
16. Implement `HierarchyService`
17. Implement `HierarchyTools` (all 4 tools)
18. **Test:** find_implementations, get_type_hierarchy work across projects

### Phase 5: Reference Tools (Implements: FR-4)
19. Implement `ReferencesService`
20. Implement `ReferenceTools` (all 3 tools)
21. **Test:** find_references, find_callers work with context snippets

### Phase 6: Polish (Implements: NFR-2 through NFR-5)
22. Error handling polish across all tools
23. Performance testing with a real solution
24. Cache invalidation testing
25. Configure for Claude Code (`claude_desktop_config.json` / `CLAUDE.md` example)
26. Write README with setup instructions

---

## 8. Claude Code Integration

### Configuration (in user's project or global config)

```json
{
  "mcpServers": {
    "sharpmcp": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/SharpMCP/SharpMCP.csproj"],
      "env": {}
    }
  }
}
```

### Or after publishing as a tool:

```json
{
  "mcpServers": {
    "sharpmcp": {
      "command": "/path/to/SharpMCP"
    }
  }
}
```

---

## 9. Key Design Decisions & Rationale

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Single project | Yes | 17 tools + 6 services is not large enough to warrant multi-project. Keeps build simple. |
| Roslyn 5.0.0 | Stable | Ships with .NET 9 SDK. Latest stable release, full feature set. |
| MSBuildWorkspace | Yes | Only way to load real .sln/.csproj with full project references and NuGet. AdhocWorkspace can't do this. |
| Cache per solution path | Dictionary | Simple, effective for single-user stdio use. No need for distributed cache. |
| Tools return strings | Not structured JSON | MCP tool results are text for LLM consumption. Compact text is more token-efficient than JSON for most queries. |
| SymbolFinder API | Use Roslyn built-in | `SymbolFinder.FindImplementationsAsync`, `FindDerivedClassesAsync`, `FindCallersAsync`, `FindReferencesAsync` are battle-tested APIs. No need to reinvent. |
| Separate tool classes | By feature group | 5 classes × 3-6 methods each is manageable. Groups map to mental model. Easy to add/remove tools. |
| Formatter as separate layer | Yes | Same symbol data can be formatted compact or full. Keeps tool logic clean. Centralizes all output formatting. |

---

## 10. Future Extensibility (v2+)

The architecture supports these additions without major refactoring:

- **Write tools:** Add `RefactoringService` + `RefactoringTools` class. WorkspaceManager already has the MSBuildWorkspace with `TryApplyChanges`.
- **HTTP transport:** Change `WithStdioServerTransport()` to `WithHttpTransport()` + add `ModelContextProtocol.AspNetCore`. No service changes needed.
- **File watching:** Add `FileSystemWatcher` in WorkspaceManager for proactive cache invalidation.
- **Multi-language:** Add VB/F# Workspaces packages. Services would need language-agnostic symbol handling (most Roslyn APIs are already language-agnostic at the ISymbol level).
