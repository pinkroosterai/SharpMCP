# SharpMCP Architecture

Roslyn-powered MCP server for C# code intelligence. Provides 20 tools that let LLMs query codebase structure without reading entire files.

## System Overview

```
                    stdio transport
  LLM Client  ─────────────────────────►  MCP Server (Program.cs)
                                                │
                                         ┌──────┴──────┐
                                         │  Tool Layer  │  7 classes, 20 tools
                                         │  (thin MCP   │  Parse params, catch errors,
                                         │   wrappers)  │  format output
                                         └──────┬──────┘
                                                │
                                         ┌──────┴──────┐
                                         │  Formatting  │  SymbolFormatter
                                         │   Layer      │  LocationFormatter
                                         └──────┬──────┘
                                                │
                                         ┌──────┴──────┐
                                         │  Service     │  11 singletons + 1 static
                                         │   Layer      │  All Roslyn logic
                                         └──────┬──────┘
                                                │
                                         ┌──────┴──────┐
                                         │  Workspace   │  MSBuildWorkspace
                                         │   Manager    │  Caching + invalidation
                                         └──────┬──────┘
                                                │
                                         ┌──────┴──────┐
                                         │   Roslyn     │  Compilations, Symbols,
                                         │  Compiler    │  SemanticModels, Syntax
                                         └─────────────┘
```

## Dependency Graph

```
Program.cs (DI registration)
  │
  ├─ ProjectTools ──────────► ProjectService ──────────► WorkspaceManager
  ├─ SymbolTools ───────────► SymbolSearchService ─────► WorkspaceManager
  │                                                ────► SymbolResolver ──► WorkspaceManager
  ├─ HierarchyTools ────────► HierarchyService ────────► WorkspaceManager
  │                                                ────► SymbolResolver
  ├─ ReferenceTools ────────► ReferencesService ───────► WorkspaceManager
  │                                                ────► SymbolResolver
  ├─ SourceTools ───────────► SourceService ───────────► SymbolResolver
  ├─ RefactoringTools ──────► RenameService ───────────► WorkspaceManager
  │                     │                          ────► SymbolResolver
  │                     ├───► InterfaceService ────────► WorkspaceManager
  │                     │                          ────► SymbolResolver
  │                     └───► SignatureService ────────► WorkspaceManager
  │                                                ────► SymbolResolver
  └─ AnalysisTools ─────────► AnalysisService ─────────► WorkspaceManager
                                  └──► CodeSmellChecks (static) ──► SymbolResolver.GetAllNamedTypes
                                                               ──► AnalysisService.HasExcludedAttribute
                                                               ──► LocationFormatter.MakePathRelative
```

All tool classes also depend on `SymbolFormatter` (except `SourceTools` and `AnalysisTools`).

## Layer Details

### 1. Entry Point — `Program.cs` (40 LOC)

```
MSBuildLocator.RegisterDefaults()     ← MUST be first line
Host.CreateApplicationBuilder()
  → 12 singleton service registrations
  → AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()
Build().RunAsync()
```

Critical: `MSBuildLocator.RegisterDefaults()` must execute before any Roslyn types are loaded into the CLR. Moving this line will cause runtime failures.

### 2. Tool Layer — 7 Classes, 20 Tools

Thin MCP wrappers. Every tool method follows the same pattern:

```csharp
[McpServerTool(Name = "tool_name"), Description("...")]
public async Task<string> ToolName(
    [Description("...")] string solutionPath,
    /* other params */
    [Description("...")] string detail = "compact")
{
    try
    {
        var detailLevel = DetailLevelExtensions.Parse(detail);
        var result = await _service.DoWorkAsync(solutionPath, ...);
        return _formatter.FormatResult(result, detailLevel);
    }
    catch (Exception ex)
    {
        return $"Error: {ex.Message}";
    }
}
```

**Cross-cutting patterns:**
- 19 of 20 tools require `solutionPath`; exception: `get_file_content` takes bare `filePath`
- 14 tools accept `detail` parameter ("compact" | "full")
- 5 tools accept optional `typeName` for disambiguation
- Tools never throw — all exceptions become `"Error: ..."` strings

### 3. Formatting Layer — 2 Classes

**`SymbolFormatter`** (257 LOC) — Central output formatting.

| Method | Compact output | Full adds |
|--------|---------------|-----------|
| `FormatSymbol` | `<signature>  [path:line]` | Doc summary + attributes |
| `FormatSymbolList` | One line per symbol | Doc comment + indented source body |
| `FormatReferenceList` | `path:line [in Container] - snippet` | Context lines before/after |
| `FormatTypeHierarchy` | Type + bases + interfaces | (always same) |
| `FormatProjectList` | Column-aligned project table | (always same) |
| `FormatDiagnosticList` | `Severity Id: Message [path:line]` | (always same) |

Static helpers used by services: `GetSignature`, `GetKindString`, `BuildSymbolResult`, `ExtractSummary`, `GetSourceBodyAsync`.

**`LocationFormatter`** (61 LOC) — Location → display text.

| Method | Output |
|--------|--------|
| `FormatLocation` | `"relative/path/File.cs:42"` or `"(no source)"` |
| `FormatWithSnippet` | `"path:42 - <trimmed line>"` |
| `MakePathRelative` | URI-based relative path computation |

### 4. Service Layer — 11 Singletons + 1 Static Utility

#### WorkspaceManager (192 LOC) — Workspace lifecycle

The foundation service. Loads `.sln`/`.csproj` files via MSBuildWorkspace.

```
API:
  GetSolutionAsync(solutionPath) → Solution
  GetCompilationAsync(solutionPath, projectName?) → Compilation
  GetProjectAsync(solutionPath, projectName) → Project
  ApplyChangesAsync(solutionPath, newSolution)
  InvalidateCacheAsync(solutionPath)
```

**Caching:** `Dictionary<string, CachedWorkspace>` keyed on normalized path, guarded by `SemaphoreSlim`. Staleness check: 5-second gated filesystem scan comparing `*.cs` write times against `LoadedAt`. Stale entries are disposed and reloaded.

**Write path:** `ApplyChangesAsync` → `workspace.TryApplyChanges()` → updates cache with `CurrentSolution`.

#### SymbolResolver (145 LOC) — Symbol lookup

```
API:
  ResolveTypeAsync(solutionPath, typeName) → INamedTypeSymbol
  ResolveSymbolAsync(solutionPath, symbolName, containingType?) → ISymbol
  ResolveMethodAsync(solutionPath, methodName, typeName?) → IMethodSymbol
  static GetAllNamedTypes(compilation) → IEnumerable<INamedTypeSymbol>
```

Matches on both short name and fully qualified name. Throws with candidate list on ambiguity. `GetAllNamedTypes` recursively traverses all namespaces including nested types.

#### SymbolSearchService (155 LOC) — Search + browse

```
API:
  FindSymbolsAsync(solutionPath, query, kind?, exact?, detail?) → List<SymbolResult>
  GetFileSymbolsAsync(solutionPath, filePath, depth) → List<SymbolResult>
  GetTypeMembersAsync(solutionPath, typeName, detail) → List<SymbolResult>
  ListNamespacesAsync(solutionPath) → List<string>
```

Case-insensitive substring search (or exact match when `exact=true`). When `exact=true`, returns first source-defined match with full detail. Deduplicates across projects via `HashSet<string>` on `"displayString|kind"`. Filters `IsImplicitlyDeclared` and compiler-generated members (`name.StartsWith("<")`). `IsInSource` filter applied to all symbol results.

#### ProjectService (140 LOC) — Project metadata

```
API:
  ListProjectsAsync(solutionPath) → List<ProjectInfo>
  GetProjectInfoAsync(solutionPath, projectName) → ProjectInfo  (includes PackageReferences)
  ListSourceFilesAsync(solutionPath, projectName) → List<string>
  GetDiagnosticsAsync(solutionPath, projectName?) → List<DiagnosticInfo>
  private ParsePackageReferences(XDocument?) → List<PackageInfo>
```

Parses `.csproj` XML via `XDocument.Load` for framework/output type. `GetProjectInfoAsync` returns `ProjectInfo` with `PackageReferences` field populated by `ParsePackageReferences`. Diagnostics filtered to Warning+ severity, sorted errors-first.

#### SourceService (59 LOC) — Source retrieval

```
API:
  GetSymbolSourceAsync(solutionPath, symbolName, typeName?) → string
  static GetFileContentAsync(filePath, startLine?, endLine?) → string
```

Uses `DeclaringSyntaxReferences` → `GetSyntaxAsync()` → `ToFullString()`. File content returns line-numbered format with 5MB guard and clamped line ranges.

#### HierarchyService (108 LOC) — Type relationships

```
API:
  FindDerivedTypesAsync(solutionPath, typeName, detail) → (List<SymbolResult>, string TypeKind)
  GetTypeHierarchyAsync(solutionPath, typeName) → TypeHierarchyResult
  FindOverridesAsync(solutionPath, typeName, methodName, detail) → List<SymbolResult>
```

`FindDerivedTypesAsync` replaces `FindImplementationsAsync` and `FindSubclassesAsync`. Auto-detects interface vs class via `TypeKind` and dispatches to `SymbolFinder.FindImplementationsAsync` or `FindDerivedClassesAsync` accordingly. All results filtered to `IsInSource` to exclude metadata symbols.

#### ReferencesService (155 LOC) — Reference tracking

```
API:
  FindReferencesAsync(solutionPath, symbolName, typeName?, projectScope?, detail, mode) → List<ReferenceResult>
    mode: "all" (default) | "callers" | "usages"
  private FindReferencesInternalAsync(...)  → SymbolFinder.FindReferencesAsync path
  private FindCallersInternalAsync(...)     → SymbolFinder.FindCallersAsync path
```

Single public method with `mode` parameter dispatches to internal implementations. Full detail adds 2-line context before/after. Each result includes `ContainingSymbol` via `GetEnclosingSymbol`.

#### RenameService (181 LOC) — Symbol renaming

```
API:
  RenameSymbolAsync(solutionPath, symbolName, newName, typeName?, includeStrings) → string
```

Uses `Renamer.RenameSymbolAsync(solution, symbol, SymbolRenameOptions, newName)`. Name validated with regex `^@?[\p{L}_][\p{L}\p{Nd}_]*$`. Restricted to NamedType/Method/Property/Field/Event. File rename done via `File.Move` when type name matches filename. Invalidates cache after apply.

#### InterfaceService (345 LOC) — Interface extraction/implementation

```
API:
  ExtractInterfaceAsync(solutionPath, typeName, interfaceName?, apply) → string
  ImplementInterfaceAsync(solutionPath, typeName, interfaceName?) → string
```

`ExtractInterface`: Collects public non-static methods/properties/events. Generates interface via StringBuilder (handles generics, ref/out/in/params, init setters). When `apply=true`, creates file + adds `: IFoo` to class via AST rewriting.

`ImplementInterface`: Finds unimplemented members via `FindImplementationForInterfaceMember`. Inserts stubs (`throw new NotImplementedException()`) via text insertion at closing brace position.

#### AnalysisService (431 LOC) — Dead code + code smell analysis

```
API:
  FindUnusedCodeAsync(solutionPath, scope, projectName?) → string
  FindCodeSmellsAsync(solutionPath, category, projectName?, deep) → string
```

`FindUnusedCodeAsync`: Iterates all source symbols, runs `FindReferencesAsync` per symbol. Smart exclusion filters: entry points (`Main`, `Program`), test attributes (`[Fact]`, `[Test]`), MCP attributes, interface implementations (via `AllInterfaces` walk), overrides, public types (conservative — may be externally consumed), public const fields.

`FindCodeSmellsAsync`: Orchestrates 13 smell checks via `CodeSmellChecks` static class. Dispatches checks by category filter, collects `SmellResult` records, formats output grouped by severity (critical → warning → info).

#### CodeSmellChecks (533 LOC) — Static smell detection utility

Not a DI service. Contains all 13 smell detection methods, shared helpers, and the `MethodBodyMetrics` single-pass analyzer.

**Internal records:** `SmellResult`, `MethodBodyMetrics`

**Complexity checks:** Long method (>50/100 lines), large class (>20/40 members), long parameter list (>5/8 params), deep nesting (>3/5 levels), high cyclomatic complexity (>10/20).

**Design checks:** God class (>20 members + >5 deps), data class (0 methods, ≥2 properties), middle man (>80% delegation), too many dependencies (>5/8 constructor params), feature envy (deep only — semantic member access analysis).

**Inheritance checks:** Deep inheritance (>3 levels), refused bequest (<20% override rate), speculative generality (unused type parameters).

**Key optimization:** `CheckMethodBodySmellsAsync` calls `AnalyzeMethodBodyAsync` once per method and evaluates 4 smells (long method, nesting, complexity, middle man) from the same `MethodBodyMetrics`.

#### SignatureService (415 LOC) — Signature refactoring

```
API:
  ChangeSignatureAsync(solutionPath, methodName, typeName?, addParameters?, removeParameters?, reorderParameters?) → string
```

Text-based replacement (not Roslyn solution mutation). Modifies declaration first, then call sites in descending position order to avoid index shifts. Custom comma-splitter respects generic angle-bracket nesting. Handles named arguments. New params without defaults get `default(T)` at call sites.

### 5. Models — 5 Files, 7 Types

| Type | Fields |
|------|--------|
| `DetailLevel` (enum) | `Compact`, `Full` |
| `ProjectInfo` (record) | Name, FilePath, TargetFramework, OutputType, SourceFileCount, ProjectReferences, PackageReferences |
| `PackageInfo` (record) | Name, Version |
| `DiagnosticInfo` (record) | Id, Severity, Message, FilePath, Line |
| `SymbolResult` (record) | Name, FullyQualifiedName, Kind, Signature, FilePath, Line, DocComment?, SourceBody? |
| `ReferenceResult` (record) | FilePath, Line, Column, CodeSnippet, ContextBefore?, ContextAfter?, ContainingSymbol? |
| `TypeHierarchyResult` (record) | TypeName, Kind, BaseTypes, Interfaces, Members? |

## Tool Reference

### Project Structure — `ProjectTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `list_projects` | solutionPath | Project table: name, framework, output type, file count, refs |
| `get_project_info` | solutionPath, projectName | Single project detail (includes package references) |
| `list_source_files` | solutionPath, projectName | .cs file path list |
| `get_diagnostics` | solutionPath, projectName? | Errors + warnings (all projects if name omitted) |

### Symbol Search — `SymbolTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `find_symbol` | solutionPath, query, kind?, exact?, detail | Matching symbols (substring or exact match) |
| `get_file_symbols` | solutionPath, filePath, depth?, detail | Symbols in file (depth=1 for members) |
| `get_type_members` | solutionPath, typeName, detail | Methods, properties, fields, events of a type |
| `list_namespaces` | solutionPath | All source-defined namespaces |

### Type Hierarchy — `HierarchyTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `find_derived_types` | solutionPath, typeName, detail | Implementations (interface) or subclasses (class), auto-detected |
| `get_type_hierarchy` | solutionPath, typeName | Full base chain + all interfaces |
| `find_overrides` | solutionPath, typeName, methodName, detail | Override locations |

### References — `ReferenceTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `find_references` | solutionPath, symbolName, typeName?, projectScope?, detail, mode? | Reference locations (mode: "all", "callers", "usages") |

### Source — `SourceTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `get_source` | solutionPath, symbolName, typeName? | Symbol source code |
| `get_file_content` | filePath, startLine?, endLine? | File content with line numbers |

### Refactoring — `RefactoringTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `rename_symbol` | solutionPath, symbolName, newName, typeName?, includeStrings | Summary of renamed refs + files |
| `extract_interface` | solutionPath, typeName, interfaceName?, apply | Generated interface code + change summary |
| `implement_interface` | solutionPath, typeName, interfaceName? | List of added stub members |
| `change_signature` | solutionPath, methodName, typeName?, addParameters?, removeParameters?, reorderParameters? | Before/after signature + updated call site count |

### Analysis — `AnalysisTools.cs`

| Tool | Parameters | Returns |
|------|-----------|---------|
| `find_unused_code` | solutionPath, scope?, projectName? | Grouped list of unreferenced symbols |
| `find_code_smells` | solutionPath, category?, projectName?, deep? | Severity-grouped list of code smells |

## Key Design Decisions

1. **Text output, not JSON.** Tools return human-readable text. More token-efficient for LLM consumption than structured JSON with keys and quotes.

2. **Compact by default.** The `detail` parameter defaults to `"compact"` (signatures + locations). `"full"` adds source bodies and doc comments — use only when needed.

3. **Cached workspaces.** `WorkspaceManager` caches loaded solutions with 5-second filesystem staleness checks. First query is slow (full compilation); subsequent queries are fast.

4. **Text-based writes for complex refactoring.** `SignatureService` and `InterfaceService.ImplementInterface` use text splicing rather than Roslyn AST mutation. Chosen for reliability with complex syntax transformations.

5. **Conservative unused-code filtering.** `AnalysisService` skips public types, interface implementations, overrides, and attributed symbols. Trades recall for precision — better to miss dead code than to report false positives.

7. **Single-pass method body analysis.** `CodeSmellChecks.AnalyzeMethodBodyAsync` walks each method body once and extracts line count, nesting depth, cyclomatic complexity, and delegation status. Four smell checks share the same metrics — no redundant syntax walking.

6. **Source-only filtering.** Hierarchy and namespace queries filter to `IsInSource` locations, excluding metadata/BCL symbols that would be noise.

## Dependencies

| Package | Version | Role |
|---------|---------|------|
| ModelContextProtocol | 0.8.0-preview.1 | MCP SDK: `[McpServerToolType]`, stdio transport |
| Microsoft.Extensions.Hosting | 9.0.* | Host builder, DI container, logging |
| Microsoft.CodeAnalysis.CSharp.Workspaces | 5.0.0 | Roslyn: syntax, semantics, compilations |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.0.0 | MSBuildWorkspace for .sln/.csproj |
| Microsoft.Build.Locator | 1.11.2 | Finds MSBuild at runtime |
| Microsoft.Build.Framework | 17.11.31 | Compile-only (ExcludeAssets="runtime") |

## File Summary

| Directory | Files | LOC | Purpose |
|-----------|-------|-----|---------|
| `Program.cs` | 1 | 40 | Entry point + DI |
| `Tools/` | 7 | 652 | MCP tool definitions |
| `Services/` | 12 | 2,737 | Business logic + smell detection |
| `Formatting/` | 2 | 318 | Output formatting |
| `Models/` | 5 | 65 | Shared DTOs |
| **Total** | **27** | **3,934** | |
