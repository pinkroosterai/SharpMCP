# Requirements: Tool Consolidation (26 → 19 tools)

Date: 2026-02-19

## Overview

Reduce the MCP tool surface from 26 to 19 tools by merging functionally overlapping tools, while also refactoring the service layer to eliminate internal duplication and fix a bug. Clean break — no backward-compatible aliases.

## Consolidation 1: References (3 → 1)

### Before

| Tool | Parameters |
|------|-----------|
| `find_references` | solutionPath, symbolName, typeName?, projectScope?, detail |
| `find_callers` | solutionPath, methodName, typeName?, detail |
| `find_usages` | solutionPath, typeName, detail |

### After: `find_references`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `symbolName` | string | yes | — | Symbol name to find references for |
| `typeName` | string | no | null | Containing type for disambiguation |
| `mode` | string | no | "all" | `"all"` = all references, `"callers"` = call sites only (methods), `"usages"` = type usage sites only |
| `projectScope` | string | no | null | Restrict search to a specific project |
| `detail` | string | no | "compact" | `"compact"` or `"full"` |

### Behavior by mode

- **`all`**: Current `find_references` behavior. Uses `SymbolFinder.FindReferencesAsync`. Works for any symbol kind.
- **`callers`**: Current `find_callers` behavior. Uses `SymbolFinder.FindCallersAsync`. Validates symbol is a method; returns error if not.
- **`usages`**: Current `find_usages` behavior. Uses `SymbolFinder.FindReferencesAsync` (same as `all`). Intended for types but works for any symbol.

### Output format

Header changes per mode:
- `all`: `"References to {symbolName} ({count}):"`
- `callers`: `"Callers of {symbolName} ({count}):"`
- `usages`: `"Usages of {symbolName} ({count}):"`

Body format is the same across all modes (current reference formatting).

### Service changes

`ReferencesService`:
- Remove `FindCallersAsync` and `FindUsagesAsync` public methods
- Rename `FindReferencesForSymbolAsync` → keep as the private core
- Add `mode` parameter to `FindReferencesAsync` that dispatches:
  - `"all"` / `"usages"` → existing `FindReferencesForSymbolAsync` path
  - `"callers"` → existing `FindCallersAsync` logic (SymbolFinder.FindCallersAsync)
- Eliminate duplicated formatting code from the old `FindCallersAsync`

---

## Consolidation 2: Hierarchy (2 → 1)

### Before

| Tool | Parameters |
|------|-----------|
| `find_implementations` | solutionPath, interfaceName, detail |
| `find_subclasses` | solutionPath, baseClassName, detail |

### After: `find_derived_types`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Interface or base class name |
| `detail` | string | no | "compact" | `"compact"` or `"full"` |

### Behavior

Resolves `typeName` → inspects `TypeKind`:
- **Interface** → calls `SymbolFinder.FindImplementationsAsync` (current `find_implementations` behavior)
- **Class** → calls `SymbolFinder.FindDerivedClassesAsync` (current `find_subclasses` behavior)
- **Other** → returns `"Error: '{typeName}' is not an interface or class."`

### Output format

- Interface: `"Classes implementing {typeName} ({count}):"`
- Class: `"Classes extending {typeName} ({count}):"`

### Service changes

`HierarchyService`:
- Remove `FindImplementationsAsync` and `FindSubclassesAsync`
- Add `FindDerivedTypesAsync(solutionPath, typeName, detailLevel)` that dispatches based on resolved type kind
- Extract shared filtering + result-building logic into private helper

---

## Consolidation 3: Project Metadata (6 → 4)

### Before

| Tool | Unique output |
|------|--------------|
| `list_projects` | All projects with summary |
| `get_project_info` | Single project metadata |
| `list_project_references` | Project-to-project deps |
| `list_package_references` | NuGet packages |
| `list_source_files` | .cs file listing |
| `get_diagnostics` | Compilation diagnostics |

### After: Fold `list_project_references` and `list_package_references` into `get_project_info`

Remove:
- `list_project_references` — output folded into `get_project_info`
- `list_package_references` — output folded into `get_project_info`

Keep (unchanged):
- `list_projects` — solution-level overview
- `list_source_files` — file listing (different enough)
- `get_diagnostics` — compilation analysis (different enough)

### Modified: `get_project_info`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `projectName` | string | yes | — | Project name |

### New output format

```
Project: MyService
  Framework: net9.0
  Output: Exe
  Source files: 42

  Project references (3):
    MyLib
    SharedModels
    Utilities

  Package references (5):
    Microsoft.Extensions.Hosting 9.0.1
    Newtonsoft.Json 13.0.3
    ...
```

Always includes both project and package references (they're small and useful together). No additional parameter needed.

### Service changes

`ProjectService.GetProjectInfoAsync`:
- Inline the logic from `ListProjectReferencesAsync` and `ListPackageReferencesAsync` into the formatted output
- Remove the now-unused public methods

---

## Consolidation 4: Symbol Search (2 → 1)

### Before

| Tool | Parameters | Matching |
|------|-----------|---------|
| `find_symbol` | solutionPath, query, kind?, detail | Case-insensitive substring |
| `get_symbol_info` | solutionPath, symbolName, detail | Exact name match via `GetSymbolsWithName` |

### After: `find_symbol` (enhanced)

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `query` | string | yes | — | Symbol name or substring |
| `kind` | string | no | null | Filter: class, interface, method, property, field, enum, struct, event |
| `exact` | bool | no | false | `true` = exact name match (like old get_symbol_info), `false` = substring match |
| `detail` | string | no | "compact" | `"compact"` or `"full"` |

### Behavior

- **`exact=false`** (default): Current `find_symbol` behavior. `compilation.GetSymbolsWithName(s => s.Contains(query, OrdinalIgnoreCase))`. Returns list of matches.
- **`exact=true`**: Current `get_symbol_info` behavior. `compilation.GetSymbolsWithName(query, SymbolFilter.All, NameMatchOptions)`. Returns first source-defined match with detailed info.

### Service changes

`SymbolSearchService`:
- Remove `GetSymbolInfoAsync`
- Add `exact` parameter to `FindSymbolsAsync`
- When `exact=true`: use exact name matching, filter to `IsInSource`, return first match with full formatting

---

## Summary: Tool Inventory After Consolidation

### Project Structure (4 tools, was 6)

| Tool | Description |
|------|-------------|
| `list_projects` | List all projects in solution with metadata |
| `get_project_info` | Project metadata including references and packages |
| `list_source_files` | All .cs files in a project |
| `get_diagnostics` | Compilation errors and warnings |

### Symbol Search (4 tools, was 5)

| Tool | Description |
|------|-------------|
| `find_symbol` | Search by name (substring or exact), filter by kind |
| `get_file_symbols` | All symbols in a file |
| `get_type_members` | Members of a type |
| `list_namespaces` | All namespaces in solution |

### Type Hierarchy (3 tools, was 4)

| Tool | Description |
|------|-------------|
| `find_derived_types` | Find implementations (interfaces) or subclasses (classes) |
| `get_type_hierarchy` | Full inheritance chain |
| `find_overrides` | Overrides of virtual/abstract method |

### References (1 tool, was 3)

| Tool | Description |
|------|-------------|
| `find_references` | Find references, callers, or usages (mode parameter) |

### Source Retrieval (2 tools, unchanged)

| Tool | Description |
|------|-------------|
| `get_source` | Source code for a specific symbol |
| `get_file_content` | File content with line numbers |

### Refactoring (4 tools, unchanged)

| Tool | Description |
|------|-------------|
| `rename_symbol` | Rename + update all references |
| `extract_interface` | Generate interface from class |
| `implement_interface` | Stub unimplemented members |
| `change_signature` | Modify params + update call sites |

### Analysis (2 tools, unchanged)

| Tool | Description |
|------|-------------|
| `find_unused_code` | Detect unreferenced symbols |
| `find_code_smells` | Detect code smells by severity |

**Total: 20 tools** (was 26, −6 tools removed, −1 merged pair = net −7)

Wait — let me recount:
- References: 3 → 1 (−2)
- Hierarchy: 4 → 3 (−1)
- Project: 6 → 4 (−2)
- Symbol: 5 → 4 (−1)
- Source: 2 → 2 (0)
- Refactoring: 4 → 4 (0)
- Analysis: 2 → 2 (0)

Total: 26 − 2 − 1 − 2 − 1 = **20 tools**

Correction: the count comes to 20, not 19. The original estimate of 19 assumed merging find_symbol + get_symbol_info completely removed one more, but we're actually keeping the 4 SymbolTools (find_symbol absorbs get_symbol_info = −1, so 5→4). Let me recheck:

Before: 6 + 5 + 4 + 3 + 2 + 4 + 2 = 26
After:  4 + 4 + 3 + 1 + 2 + 4 + 2 = 20

That's −6, yielding 20 tools. The "aggressive −7" estimate was slightly off because folding 2 project tools into get_project_info removes 2 tools (not 3). Let me correct this in the doc.

---

## Service-Layer Refactoring (Non-Tool-Changing)

These changes improve internal code quality without changing the tool surface.

### S1. Fix `FindSymbolsAsync` missing `IsInSource` filter (Bug fix)

`SymbolSearchService.FindSymbolsAsync` does not filter symbols to `IsInSource`, so it can return metadata-only symbols (e.g., from referenced assemblies). Add the same `symbol.Locations.Any(l => l.IsInSource)` filter used by `ListNamespacesAsync` and `GetSymbolInfoAsync`.

### S2. Consolidate compilation iteration pattern

Extract a shared helper used by SymbolSearchService, SymbolResolver, and AnalysisService:

```csharp
// In WorkspaceManager or a new CompilationHelper
async IAsyncEnumerable<Compilation> GetCompilationsAsync(Solution solution, string? projectName = null)
```

This replaces the repeated pattern of:
```csharp
foreach (var project in solution.Projects)
{
    var compilation = await project.GetCompilationAsync();
    if (compilation == null) continue;
    ...
}
```

### S3. Standardize symbol deduplication

`FindSymbolsAsync` uses `HashSet<string>` with key `"{displayString}|{kind}"`.
`ResolveTypeAsync` uses `SymbolEqualityComparer.Default`.

Standardize on `HashSet<string>` with display-string key (more robust for cross-project dedup where the same type may appear in multiple compilations as different `ISymbol` instances).

### S4. Extract shared parameter formatting

`InterfaceService.FormatMethodSignature` and `SignatureService.ComputeNewParameterList` both format method parameters. Extract a shared `ParameterFormatter` utility in the Formatting namespace.

### S5. Consolidate ReferencesService formatting

After merging the 3 reference tools into 1, the `FindCallersAsync` formatting duplication is automatically resolved since there's a single code path with mode dispatch.

---

## Files Changed

| File | Action | Description |
|------|--------|-------------|
| `Tools/ReferenceTools.cs` | MODIFY | 3 methods → 1 (`find_references` with `mode`) |
| `Tools/HierarchyTools.cs` | MODIFY | Remove `find_implementations` + `find_subclasses`, add `find_derived_types` |
| `Tools/ProjectTools.cs` | MODIFY | Remove `list_project_references` + `list_package_references` |
| `Tools/SymbolTools.cs` | MODIFY | Remove `get_symbol_info`, add `exact` param to `find_symbol` |
| `Services/ReferencesService.cs` | MODIFY | Merge 3 public methods → 1 with mode dispatch |
| `Services/HierarchyService.cs` | MODIFY | Merge impl + subclass methods, extract shared helper |
| `Services/ProjectService.cs` | MODIFY | Fold refs/packages into GetProjectInfoAsync |
| `Services/SymbolSearchService.cs` | MODIFY | Remove GetSymbolInfoAsync, add exact mode to FindSymbolsAsync, fix IsInSource bug |
| `Formatting/ParameterFormatter.cs` | CREATE | Shared parameter formatting utility |

No changes to: SourceTools, RefactoringTools, AnalysisTools, AnalysisService, CodeSmellChecks, RenameService, WorkspaceManager, SymbolResolver, SymbolFormatter, LocationFormatter, Models.

## Acceptance Criteria

1. `dotnet build src/SharpMCP` — 0 warnings, 0 errors
2. Tool count is 20 (verify by listing `[McpServerTool]` attributes)
3. `find_references` with `mode=all` returns same results as old `find_references`
4. `find_references` with `mode=callers` returns same results as old `find_callers`
5. `find_references` with `mode=usages` returns same results as old `find_usages`
6. `find_derived_types` on an interface returns same results as old `find_implementations`
7. `find_derived_types` on a class returns same results as old `find_subclasses`
8. `get_project_info` output includes project references and package references
9. `find_symbol` with `exact=true` returns same results as old `get_symbol_info`
10. `find_symbol` with `exact=false` (default) returns same results as before
11. `find_symbol` no longer returns metadata-only symbols (bug fix verified)
