# Implementation Workflow: SharpMCP

**Date:** 2026-02-19
**Source:** `design_sharpmcp_20260219.md`
**Strategy:** Systematic, incremental phases with validation checkpoints

---

## Phase Overview

```
Phase 1: Project Scaffold          ████░░░░░░░░░░░░░░░░  ~10%
Phase 2: Core Infrastructure       ████████░░░░░░░░░░░░  ~25%
Phase 3: Project Tools             ████████████░░░░░░░░  ~15%
Phase 4: Symbol Tools              ████████████████░░░░  ~20%
Phase 5: Hierarchy Tools           ██████████████████░░  ~15%
Phase 6: Reference Tools           ████████████████████  ~10%
Phase 7: Polish & Integration      ████████████████████  ~5%
```

---

## Phase 1: Project Scaffold

**Goal:** Bootable MCP server process that responds to stdio, with empty tool surface.

### Task 1.1: Create .NET 9 console project
```
Files: SharpMCP.csproj
Actions:
  - dotnet new console -n SharpMCP
  - Set TargetFramework to net9.0
  - Add all NuGet packages:
    • ModelContextProtocol (--prerelease)
    • Microsoft.Extensions.Hosting 9.0.0
    • Microsoft.CodeAnalysis.CSharp 5.0.0
    • Microsoft.CodeAnalysis.CSharp.Workspaces 5.0.0
    • Microsoft.CodeAnalysis.Workspaces.MSBuild 5.0.0
    • Microsoft.Build.Locator 1.7.8
  - dotnet restore && dotnet build (verify clean compile)
```

### Task 1.2: Implement Program.cs bootstrap
```
Files: Program.cs
Actions:
  - MSBuildLocator.RegisterDefaults() (FIRST LINE, before any Roslyn types)
  - Host.CreateApplicationBuilder(args)
  - Configure logging to stderr (LogToStandardErrorThreshold = LogLevel.Trace)
  - AddMcpServer() with ServerInfo { Name = "SharpMCP", Version = "1.0.0" }
  - WithStdioServerTransport()
  - WithToolsFromAssembly()
  - builder.Build().RunAsync()
```

### Task 1.3: Create folder structure
```
Actions:
  - mkdir Tools/ Services/ Formatting/ Models/
```

### Task 1.4: Create shared models
```
Files: Models/DetailLevel.cs, Models/SymbolResult.cs, Models/ReferenceResult.cs,
       Models/ProjectInfo.cs, Models/TypeHierarchyResult.cs
Actions:
  - DetailLevel enum (Compact, Full) + Parse helper
  - SymbolResult record
  - ReferenceResult record
  - ProjectInfo + PackageInfo + DiagnosticInfo records
  - TypeHierarchyResult record
```

### Checkpoint 1
```
Validation:
  - dotnet build succeeds with zero errors
  - dotnet run starts the process (blocks on stdin, writes nothing to stdout until input)
  - Process exits cleanly on stdin close
```

**Dependencies:** None (starting point)

---

## Phase 2: Core Infrastructure

**Goal:** WorkspaceManager loads a real solution, SymbolFormatter produces output. A single smoke-test tool proves the pipeline end-to-end.

### Task 2.1: Implement WorkspaceManager
```
Files: Services/WorkspaceManager.cs
Actions:
  - Singleton service
  - Dictionary<string, CachedWorkspace> _cache
  - CachedWorkspace inner class: Workspace, Solution, Compilations dict, LoadedAt, NormalizedPath
  - GetSolutionAsync(string path):
    • Normalize path (Path.GetFullPath)
    • Check cache: if hit AND not stale → return cached
    • If miss or stale → create MSBuildWorkspace, OpenSolutionAsync, cache result
    • Attach WorkspaceFailed handler for error collection
  - GetCompilationAsync(string solutionPath, string? projectName):
    • Get solution, find project (by name or first if null)
    • project.GetCompilationAsync(), cache in CachedWorkspace.Compilations
  - GetProjectAsync(string solutionPath, string projectName)
  - InvalidateCache(string solutionPath)
  - Cache staleness check:
    • Compare LoadedAt against max LastWriteTimeUtc of *.cs files under solution dir
    • Use Directory.EnumerateFiles with SearchOption.AllDirectories
  - Error handling:
    • FileNotFoundException → "Solution not found: {path}"
    • InvalidOperationException from MSBuild → "Run 'dotnet restore' first"
    • General exceptions → "Failed to load solution: {message}"
```

### Task 2.2: Implement SymbolFormatter (compact mode)
```
Files: Formatting/SymbolFormatter.cs, Formatting/LocationFormatter.cs
Actions:
  - SymbolFormatter.FormatSymbol(ISymbol, DetailLevel):
    Compact: "{accessibility} {kind} {name} : {bases}  [{file}:{line}]"
    Full:    above + XML doc + attributes + full signature
  - SymbolFormatter.FormatSymbolList(IEnumerable<SymbolResult>, DetailLevel)
  - LocationFormatter.FormatLocation(Location):
    "relative/path/file.cs:42"
  - LocationFormatter.FormatWithSnippet(Location, SyntaxTree, int contextLines):
    "relative/path/file.cs:42 - var svc = new OrderService();"
  - Helper: MakePathRelative(string fullPath, string solutionDir)
```

### Task 2.3: Register services in DI
```
Files: Program.cs (update)
Actions:
  - builder.Services.AddSingleton<WorkspaceManager>()
  - builder.Services.AddSingleton<SymbolFormatter>()
  - builder.Services.AddSingleton<LocationFormatter>()
```

### Task 2.4: Smoke-test tool (temporary)
```
Files: Tools/ProjectTools.cs (partial — just list_projects)
Actions:
  - Create ProjectTools class with [McpServerToolType]
  - Implement list_projects tool only:
    • Accept solutionPath
    • Call workspaceManager.GetSolutionAsync()
    • Return formatted project list
  - This proves the full pipeline: MCP → Tool → Service → Roslyn → Format → Response
```

### Checkpoint 2
```
Validation:
  - Create or use a small test .sln with 2-3 projects
  - Run SharpMCP, send a list_projects tool call (via MCP Inspector or stdin JSON-RPC)
  - Verify it returns the correct project list
  - Verify cache: second call is faster (no reload)
  - Verify error: call with nonexistent path returns clear error message
```

**Dependencies:** Phase 1 complete

---

## Phase 3: Project Tools

**Goal:** All 6 project structure tools working.

### Task 3.1: Implement ProjectService
```
Files: Services/ProjectService.cs
Actions:
  - ListProjectsAsync(solutionPath):
    • Get Solution, iterate .Projects
    • For each: name, file path, OutputType, documents count
    • Target framework: parse from Project.ParseOptions or .csproj XML
  - GetProjectInfoAsync(solutionPath, projectName):
    • Detailed info: all of above + assembly name, language, compilation options
  - ListProjectReferencesAsync(solutionPath, projectName):
    • Project.ProjectReferences → resolve to project names
  - ListPackageReferencesAsync(solutionPath, projectName):
    • Project.MetadataReferences → filter for NuGet packages
    • OR parse .csproj XML for PackageReference items (more reliable for name+version)
  - ListSourceFilesAsync(solutionPath, projectName):
    • Project.Documents → .FilePath list, sorted
  - GetDiagnosticsAsync(solutionPath, projectName?):
    • GetCompilationAsync → .GetDiagnostics()
    • Filter by severity (errors first, then warnings)
    • Format as DiagnosticInfo list
```

### Task 3.2: Complete ProjectTools (remaining 5 tools)
```
Files: Tools/ProjectTools.cs (expand)
Actions:
  - get_project_info tool
  - list_project_references tool
  - list_package_references tool
  - list_source_files tool
  - get_diagnostics tool
  - All tools: try/catch → return "Error: ..." on failure
  - All tools: validate solutionPath is not empty
```

### Task 3.3: Register ProjectService in DI
```
Files: Program.cs (update)
Actions:
  - builder.Services.AddSingleton<ProjectService>()
```

### Checkpoint 3
```
Validation:
  - All 6 project tools respond correctly against test solution
  - list_projects shows all projects with metadata
  - list_project_references shows correct dependency graph
  - get_diagnostics returns any compilation errors/warnings
  - Error cases: wrong project name → helpful "not found, available: ..." message
```

**Dependencies:** Phase 2 complete

---

## Phase 4: Symbol Tools + Source Tools

**Goal:** Symbol search, file overview, type members, source retrieval all working.

### Task 4.1: Implement helper — symbol resolution utility
```
Files: Services/SymbolResolver.cs
Actions:
  - Shared utility used by multiple services to find symbols by name
  - ResolveTypeAsync(Solution, string typeName) → INamedTypeSymbol?
    • Search across all project compilations
    • Match by simple name or fully qualified name
    • Return null if not found, throw if ambiguous (with candidates list)
  - ResolveSymbolAsync(Solution, string name, string? containingType) → ISymbol?
    • If containingType provided, find type first, then search its members
    • Otherwise search all compilations
  - ResolveMethodAsync(Solution, string methodName, string? typeName) → IMethodSymbol?
  - Helper: GetAllNamedTypes(Compilation) → IEnumerable<INamedTypeSymbol>
    • Walk compilation.GlobalNamespace recursively
```

### Task 4.2: Implement SymbolSearchService
```
Files: Services/SymbolSearchService.cs
Actions:
  - FindSymbolsAsync(solutionPath, query, kind?):
    • Get all compilations in solution
    • For each compilation, use GetSymbolsWithName(name => name.Contains(query))
    • Filter by kind if provided (class, interface, method, property, etc.)
    • Convert to SymbolResult list
  - GetFileSymbolsAsync(solutionPath, filePath, depth):
    • Find Document by file path across all projects
    • Get syntax root, walk top-level type declarations
    • If depth > 0, also include members of each type
    • Return ordered symbol list
  - GetTypeMembersAsync(solutionPath, typeName):
    • Use SymbolResolver.ResolveTypeAsync()
    • Iterate type.GetMembers(), skip compiler-generated (<...> names)
    • Build SymbolResult for each member
  - GetSymbolInfoAsync(solutionPath, symbolName):
    • Use SymbolResolver to find the symbol
    • Return full detail including doc comment, attributes, declaring file
  - ListNamespacesAsync(solutionPath):
    • Get all named types across all compilations
    • Collect distinct .ContainingNamespace.ToDisplayString()
    • Sort and return
```

### Task 4.3: Implement SourceService
```
Files: Services/SourceService.cs
Actions:
  - GetSymbolSourceAsync(solutionPath, symbolName, typeName?):
    • Use SymbolResolver to find the symbol
    • Get DeclaringSyntaxReferences[0]
    • GetSyntax() → .ToFullString()
    • Prepend file:line header
  - GetFileContentAsync(filePath, startLine?, endLine?):
    • Read file from disk (File.ReadAllLinesAsync)
    • Apply line range if provided
    • Prepend line numbers
    • Return formatted content
```

### Task 4.4: Implement SymbolTools
```
Files: Tools/SymbolTools.cs
Actions:
  - find_symbol(solutionPath, query, kind?, detail?)
  - get_file_symbols(solutionPath, filePath, depth?, detail?)
  - get_type_members(solutionPath, typeName, detail?)
  - get_symbol_info(solutionPath, symbolName, detail?)
  - list_namespaces(solutionPath)
```

### Task 4.5: Implement SourceTools
```
Files: Tools/SourceTools.cs
Actions:
  - get_source(solutionPath, symbolName, typeName?)
  - get_file_content(filePath, startLine?, endLine?)
```

### Task 4.6: Expand SymbolFormatter for full detail mode
```
Files: Formatting/SymbolFormatter.cs (expand)
Actions:
  - Full mode: include XML doc comment (extract from symbol.GetDocumentationCommentXml())
  - Full mode: include attributes list
  - Full mode: include source body for symbols
  - FormatProjectList, FormatDiagnosticList helpers
```

### Task 4.7: Register new services in DI
```
Files: Program.cs (update)
Actions:
  - builder.Services.AddSingleton<SymbolResolver>()
  - builder.Services.AddSingleton<SymbolSearchService>()
  - builder.Services.AddSingleton<SourceService>()
```

### Checkpoint 4
```
Validation:
  - find_symbol("Controller") returns all controller classes
  - find_symbol("Process", kind: "method") returns only methods
  - get_file_symbols returns correct type/method tree for a file
  - get_type_members("OrderService") returns all public members
  - get_source returns actual method body
  - get_file_content with line range returns correct slice
  - Compact vs full detail produces different output sizes
  - Symbol not found → helpful error message
```

**Dependencies:** Phase 2 complete (WorkspaceManager + SymbolFormatter)

---

## Phase 5: Hierarchy Tools

**Goal:** Type hierarchy, implementations, subclasses, overrides all working.

### Task 5.1: Implement HierarchyService
```
Files: Services/HierarchyService.cs
Actions:
  - FindImplementationsAsync(solutionPath, interfaceName):
    • Resolve interface symbol via SymbolResolver
    • Use SymbolFinder.FindImplementationsAsync(interfaceSymbol, solution)
    • Convert each INamedTypeSymbol to SymbolResult
  - FindSubclassesAsync(solutionPath, baseClassName):
    • Resolve base class symbol
    • Use SymbolFinder.FindDerivedClassesAsync(baseClassSymbol, solution)
    • Convert results
  - GetTypeHierarchyAsync(solutionPath, typeName):
    • Resolve type symbol
    • Walk .BaseType chain upward until null (collecting each)
    • Collect .AllInterfaces
    • Build TypeHierarchyResult
  - FindOverridesAsync(solutionPath, typeName, methodName):
    • Resolve the method symbol in the specified type
    • Use SymbolFinder.FindOverridesAsync(methodSymbol, solution)
    • Convert results
```

### Task 5.2: Implement HierarchyTools
```
Files: Tools/HierarchyTools.cs
Actions:
  - find_implementations(solutionPath, interfaceName, detail?)
  - find_subclasses(solutionPath, baseClassName, detail?)
  - get_type_hierarchy(solutionPath, typeName)
  - find_overrides(solutionPath, typeName, methodName, detail?)
```

### Task 5.3: Add hierarchy formatting
```
Files: Formatting/SymbolFormatter.cs (expand)
Actions:
  - FormatTypeHierarchy(TypeHierarchyResult):
    "OrderService
       bases: BaseService → object
       implements: IOrderService, IDisposable"
```

### Task 5.4: Register HierarchyService in DI
```
Files: Program.cs (update)
```

### Checkpoint 5
```
Validation:
  - find_implementations("IDisposable") returns types implementing it
  - find_subclasses("Controller") returns derived controllers
  - get_type_hierarchy shows full chain including interfaces
  - find_overrides finds all override locations
  - Cross-project: implementation in project A of interface in project B is found
```

**Dependencies:** Phase 4 complete (SymbolResolver needed)

---

## Phase 6: Reference Tools

**Goal:** Find references, callers, and usages across the solution.

### Task 6.1: Implement ReferencesService
```
Files: Services/ReferencesService.cs
Actions:
  - FindReferencesAsync(solutionPath, symbolName, typeName?, projectScope?):
    • Resolve the symbol (disambiguate with typeName if provided)
    • Get Solution from WorkspaceManager
    • Use SymbolFinder.FindReferencesAsync(symbol, solution)
    • For each ReferencedSymbol, iterate .Locations
    • For each ReferenceLocation:
      - Get Document, file path, line span
      - Extract code snippet: the line containing the reference
      - If full detail: extract ±2 lines of context
      - Determine containing symbol (method/class the reference is inside)
    • Filter by projectScope if provided
    • Sort by file path, then line number
  - FindCallersAsync(solutionPath, methodName, typeName?):
    • Resolve method symbol
    • Use SymbolFinder.FindCallersAsync(methodSymbol, solution)
    • For each CallerInfo:
      - Get calling method symbol + locations
      - Extract code snippets
    • Convert to ReferenceResult list
  - FindUsagesAsync(solutionPath, typeName):
    • Resolve type symbol
    • Use FindReferencesAsync internally
    • Return all reference locations
```

### Task 6.2: Implement ReferenceTools
```
Files: Tools/ReferenceTools.cs
Actions:
  - find_references(solutionPath, symbolName, typeName?, projectScope?, detail?)
  - find_callers(solutionPath, methodName, typeName?, detail?)
  - find_usages(solutionPath, typeName, detail?)
```

### Task 6.3: Add reference formatting
```
Files: Formatting/SymbolFormatter.cs (expand)
Actions:
  - FormatReferenceList(IEnumerable<ReferenceResult>, DetailLevel):
    Compact: "file.cs:42 in MyClass.MyMethod - var x = service.Call();"
    Full: above + ±2 lines context before/after
```

### Task 6.4: Register ReferencesService in DI
```
Files: Program.cs (update)
```

### Checkpoint 6
```
Validation:
  - find_references for a class returns all usage locations
  - find_callers for a method returns call sites with correct snippets
  - find_usages for a type shows parameter/field/variable usages
  - projectScope filter works: restricts results to named project
  - Compact output: one line per reference
  - Full output: includes surrounding context lines
```

**Dependencies:** Phase 4 complete (SymbolResolver needed)

---

## Phase 7: Polish & Integration

**Goal:** Error handling hardened, tested with Claude Code, documentation complete.

### Task 7.1: Error handling sweep
```
Files: All Tools/*.cs, Services/WorkspaceManager.cs
Actions:
  - Every tool method wrapped in try/catch → returns "Error: ..." string
  - WorkspaceManager: clear error messages for all failure modes
  - Symbol not found: suggest similar names (Levenshtein or prefix match)
  - Ambiguous symbol: list all candidates with fully qualified names
  - Empty results: return "No results found for ..." (not empty string)
```

### Task 7.2: Input validation
```
Files: All Tools/*.cs
Actions:
  - Validate solutionPath/projectPath is not null/empty
  - Validate file paths exist for file-specific tools
  - Validate detail parameter values ("compact"/"full" only)
  - Return clear validation error messages
```

### Task 7.3: Test with Claude Code
```
Actions:
  - Configure SharpMCP in Claude Code MCP settings
  - Test each tool group against a real C# solution
  - Verify LLM can discover and use tools naturally
  - Check token efficiency of compact output
  - Iterate on tool descriptions if LLM misuses tools
```

### Task 7.4: Write README.md
```
Files: README.md
Actions:
  - Project description
  - Prerequisites (.NET 9 SDK)
  - Build instructions
  - Configuration for Claude Code
  - Tool reference with examples
  - Troubleshooting (common errors)
```

### Task 7.5: Add CLAUDE.md
```
Files: CLAUDE.md
Actions:
  - Project conventions
  - Build commands
  - Architecture overview for LLM context
```

### Checkpoint 7 (Final)
```
Validation:
  - All 20 tools work correctly
  - Claude Code can discover and invoke all tools
  - Errors produce helpful messages, not stack traces
  - Compact output is token-efficient
  - README covers setup and usage
  - Clean build with zero warnings
```

**Dependencies:** All phases complete

---

## Dependency Graph

```
Phase 1: Scaffold
    │
    ▼
Phase 2: Core Infrastructure (WorkspaceManager + Formatter)
    │
    ├──────────────┬──────────────┐
    ▼              ▼              ▼
Phase 3:       Phase 4:       (Phase 4 needed for 5 & 6)
Project Tools  Symbol Tools
               + Source Tools
                   │
            ┌──────┴──────┐
            ▼             ▼
        Phase 5:      Phase 6:
        Hierarchy     Reference
        Tools         Tools
            │             │
            └──────┬──────┘
                   ▼
             Phase 7: Polish
```

**Parallelizable:** Phases 3, 4 are independent and could be built in parallel.
**Parallelizable:** Phases 5, 6 are independent (both depend on Phase 4's SymbolResolver).

---

## File Creation Order

| # | File | Phase | Purpose |
|---|------|-------|---------|
| 1 | `SharpMCP.csproj` | 1.1 | Project file |
| 2 | `Program.cs` | 1.2 | Server bootstrap |
| 3 | `Models/DetailLevel.cs` | 1.4 | Shared enum |
| 4 | `Models/SymbolResult.cs` | 1.4 | Shared record |
| 5 | `Models/ReferenceResult.cs` | 1.4 | Shared record |
| 6 | `Models/ProjectInfo.cs` | 1.4 | Shared records |
| 7 | `Models/TypeHierarchyResult.cs` | 1.4 | Shared record |
| 8 | `Services/WorkspaceManager.cs` | 2.1 | Core service |
| 9 | `Formatting/LocationFormatter.cs` | 2.2 | Path/line formatting |
| 10 | `Formatting/SymbolFormatter.cs` | 2.2 | Symbol → text |
| 11 | `Services/ProjectService.cs` | 3.1 | Project queries |
| 12 | `Tools/ProjectTools.cs` | 3.2 | MCP tools (6) |
| 13 | `Services/SymbolResolver.cs` | 4.1 | Symbol lookup utility |
| 14 | `Services/SymbolSearchService.cs` | 4.2 | Symbol search logic |
| 15 | `Services/SourceService.cs` | 4.3 | Source retrieval |
| 16 | `Tools/SymbolTools.cs` | 4.4 | MCP tools (5) |
| 17 | `Tools/SourceTools.cs` | 4.5 | MCP tools (2) |
| 18 | `Services/HierarchyService.cs` | 5.1 | Type hierarchy logic |
| 19 | `Tools/HierarchyTools.cs` | 5.2 | MCP tools (4) |
| 20 | `Services/ReferencesService.cs` | 6.1 | References logic |
| 21 | `Tools/ReferenceTools.cs` | 6.2 | MCP tools (3) |
| 22 | `README.md` | 7.4 | Documentation |
| 23 | `CLAUDE.md` | 7.5 | LLM project context |

**Total: 23 files, 20 MCP tools, 7 phases**

---

## Estimated Effort per Phase

| Phase | Tasks | Core Files | Complexity |
|-------|-------|------------|------------|
| 1. Scaffold | 4 | 6 | Low — boilerplate |
| 2. Core Infra | 4 | 3 | High — WorkspaceManager is critical |
| 3. Project Tools | 3 | 2 | Medium — straightforward Roslyn queries |
| 4. Symbol Tools | 7 | 5 | High — SymbolResolver + search logic |
| 5. Hierarchy Tools | 4 | 2 | Medium — mostly SymbolFinder API calls |
| 6. Reference Tools | 4 | 2 | Medium — SymbolFinder + snippet extraction |
| 7. Polish | 5 | 2 | Low-Medium — iteration |

---

## Next Step

Run `/sc:implement` to begin Phase 1 execution.
