# Project Index: SharpMCP

Generated: 2026-02-19

## Documentation

| Document | Tokens | Purpose |
|----------|--------|---------|
| [PROJECT_INDEX.md](PROJECT_INDEX.md) | ~2,500 | Quick-reference overview (this file) |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | ~4,000 | System design, layer details, dependency graph, design decisions |
| [docs/API_REFERENCE.md](docs/API_REFERENCE.md) | ~4,500 | Complete tool reference with all parameters and output formats |
| [docs/SERVICES.md](docs/SERVICES.md) | ~4,000 | Service internals, Roslyn API usage, data flows, edge cases |

## Project Structure

```
SharpMCP/
  src/SharpMCP/
    Program.cs              (40 LOC)  — Entry point, DI bootstrap
    Formatting/
      SymbolFormatter.cs    (257 LOC) — ISymbol → compact text
      LocationFormatter.cs  (61 LOC)  — Location → file:line text
    Models/
      DetailLevel.cs        (13 LOC)  — compact|full enum
      ProjectInfo.cs        (20 LOC)  — ProjectInfo, PackageInfo, DiagnosticInfo records
      SymbolResult.cs       (12 LOC)  — Symbol search result DTO
      ReferenceResult.cs    (11 LOC)  — Reference search result DTO
      TypeHierarchyResult.cs(9 LOC)   — Hierarchy result DTO
    Services/
      WorkspaceManager.cs   (192 LOC) — MSBuildWorkspace loading, caching, file-watch invalidation
      SymbolResolver.cs     (145 LOC) — Find types/symbols by name across projects
      SymbolSearchService.cs(155 LOC) — Symbol search with kind filtering
      ProjectService.cs     (140 LOC) — Project metadata queries
      SourceService.cs      (59 LOC)  — Source code retrieval
      HierarchyService.cs   (108 LOC) — Implementations, subclasses, overrides
      ReferencesService.cs  (155 LOC) — References, callers, usages
      RenameService.cs      (181 LOC) — Roslyn Renamer + disk apply
      InterfaceService.cs   (345 LOC) — Extract/implement interface [NEW]
      AnalysisService.cs    (431 LOC) — Unused code + code smell orchestration
      SignatureService.cs   (415 LOC) — Method signature changes
      CodeSmellChecks.cs    (533 LOC) — 13 code smell detectors (static utility)
    Tools/
      ProjectTools.cs       (126 LOC) — 4 tools: list_projects, get_project_info, list_source_files, get_diagnostics
      SymbolTools.cs        (109 LOC) — 4 tools: find_symbol, get_file_symbols, get_type_members, list_namespaces
      HierarchyTools.cs     (95 LOC)  — 3 tools: find_derived_types, get_type_hierarchy, find_overrides
      ReferenceTools.cs     (53 LOC)  — 1 tool: find_references
      SourceTools.cs        (48 LOC)  — 2 tools: get_source, get_file_content
      RefactoringTools.cs   (95 LOC)  — 4 tools: rename_symbol, extract_interface, implement_interface, change_signature
      AnalysisTools.cs      (49 LOC)  — 2 tools: find_unused_code, find_code_smells
  claudedocs/                         — Design docs & requirements
  CLAUDE.md                           — Project conventions for Claude Code
  README.md                           — Setup & tool reference
```

**Total**: ~3,850 LOC across 27 source files (excl. obj/)

## Entry Point

- **CLI**: `Program.cs` — `MSBuildLocator.RegisterDefaults()` then Host builder with DI + MCP stdio transport

## MCP Tools (20 tools across 7 classes)

### Project Structure (4) — `ProjectTools.cs`
| Tool | Description |
|------|-------------|
| `list_projects` | List all projects in solution with metadata |
| `get_project_info` | Project metadata including references and packages |
| `list_source_files` | All .cs files in a project |
| `get_diagnostics` | Compilation errors and warnings |

### Symbol Search (4) — `SymbolTools.cs`
| Tool | Description |
|------|-------------|
| `find_symbol` | Search by name (substring or exact), filter by kind |
| `get_file_symbols` | All symbols in a file (depth=1 for members) |
| `get_type_members` | Members of a type |
| `list_namespaces` | All namespaces in solution |

### Type Hierarchy (3) — `HierarchyTools.cs`
| Tool | Description |
|------|-------------|
| `find_derived_types` | Find implementations (interfaces) or subclasses (classes) |
| `get_type_hierarchy` | Full inheritance chain |
| `find_overrides` | Overrides of virtual/abstract method |

### References (1) — `ReferenceTools.cs`
| Tool | Description |
|------|-------------|
| `find_references` | Find references, callers, or usages (mode parameter) |

### Source Retrieval (2) — `SourceTools.cs`
| Tool | Description |
|------|-------------|
| `get_source` | Source code for a specific symbol |
| `get_file_content` | File content with line numbers |

### Refactoring (4) — `RefactoringTools.cs`
| Tool | Description |
|------|-------------|
| `rename_symbol` | Rename + update all references + file rename |
| `extract_interface` | Generate interface from class public members |
| `implement_interface` | Stub unimplemented interface members |
| `change_signature` | Modify params + update call sites |

### Analysis (2) — `AnalysisTools.cs`
| Tool | Description |
|------|-------------|
| `find_unused_code` | Detect unreferenced symbols |
| `find_code_smells` | Detect 13 code smells (complexity, design, inheritance) grouped by severity |

## Service Layer (11 singletons + 1 static utility)

| Service | Responsibility |
|---------|---------------|
| `WorkspaceManager` | MSBuildWorkspace lifecycle, caching, file-change invalidation |
| `SymbolResolver` | Resolve type/symbol by name across all projects |
| `SymbolSearchService` | Symbol search with kind/project filtering |
| `ProjectService` | Project metadata, packages, diagnostics |
| `SourceService` | Symbol/file source code retrieval |
| `HierarchyService` | Implementations, subclasses, hierarchy, overrides |
| `ReferencesService` | References, callers, usages via SymbolFinder |
| `RenameService` | Roslyn Renamer + apply changes to disk |
| `InterfaceService` | Extract interface + implement interface stubs |
| `AnalysisService` | Unused code detection + code smell orchestration |
| `SignatureService` | Method signature refactoring + call site updates |
| `CodeSmellChecks` | 13 code smell detectors (static utility, not DI-registered) |

## Formatting Layer (2 classes)

| Class | Purpose |
|-------|---------|
| `SymbolFormatter` | Converts `ISymbol` to compact/full text output |
| `LocationFormatter` | Converts locations to `file:line` strings |

## Models (5 files)

| Record/Enum | Fields |
|-------------|--------|
| `DetailLevel` | `Compact`, `Full` |
| `ProjectInfo` | Name, FilePath, Framework, OutputType, SourceFileCount, References |
| `PackageInfo` | Name, Version |
| `DiagnosticInfo` | Id, Message, Severity, FilePath, Line |
| `SymbolResult` | Name, Kind, FullName, FilePath, Line, Signature |
| `ReferenceResult` | FilePath, Line, CodeSnippet, ContainingSymbol |
| `TypeHierarchyResult` | BaseTypes, Interfaces, AllInterfaces |

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `ModelContextProtocol` | 0.8.0-preview.1 | MCP SDK (stdio transport, tool attributes) |
| `Microsoft.Extensions.Hosting` | 9.0.* | Host builder, DI, logging |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | 5.0.0 | Roslyn compiler platform |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | 5.0.0 | MSBuildWorkspace for .sln/.csproj loading |
| `Microsoft.Build.Locator` | 1.11.2 | Locates MSBuild at runtime |
| `Microsoft.Build.Framework` | 17.11.31 | MSBuild types (compile-only, excluded at runtime) |

## Configuration

| File | Purpose |
|------|---------|
| `SharpMCP.csproj` | Project definition, NuGet refs, MSBuild.Locator workaround |
| `.serena/project.yml` | Serena MCP project config |
| `CLAUDE.md` | Claude Code conventions & architecture notes |

## Quick Start

```bash
dotnet build src/SharpMCP        # Build
dotnet run --project src/SharpMCP # Run (stdio MCP server)
```

## Key Conventions

- Tools return `string` (not JSON) — compact text for LLM token efficiency
- All tool methods catch exceptions → return `"Error: ..."` strings
- `detail` parameter: `"compact"` (default) vs `"full"` (includes source + docs)
- `MSBuildLocator.RegisterDefaults()` MUST be first line before any Roslyn types load
- Services registered as singletons via DI
- `[McpServerToolType]` on class, `[McpServerTool(Name = "...")]` on methods
