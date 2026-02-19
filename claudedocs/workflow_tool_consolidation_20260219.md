# Workflow: Tool Consolidation (26 → 20)

Date: 2026-02-19
Spec: `claudedocs/requirements_tool_consolidation_20260219.md`

## Safety Assessment

All 7 methods being removed have **exactly 1 call site** each (their MCP tool wrapper). Zero cross-service dependencies. DI registration unchanged (services stay, tools auto-register via assembly scan). `SignatureService` calls `SymbolFinder.FindCallersAsync` (Roslyn API) directly — no conflict with `ReferencesService.FindCallersAsync`.

## Phase Overview

| Phase | What | Files | Risk |
|-------|------|-------|------|
| 1 | Merge references (3 → 1) | ReferenceTools.cs, ReferencesService.cs | Low |
| 2 | Merge hierarchy (2 → 1) | HierarchyTools.cs, HierarchyService.cs | Low |
| 3 | Fold project metadata (6 → 4) | ProjectTools.cs, ProjectService.cs | Low |
| 4 | Merge symbol search (2 → 1) + bug fix | SymbolTools.cs, SymbolSearchService.cs | Low |
| 5 | Build + verify | — | — |
| 6 | Update documentation | README, PROJECT_INDEX, docs/* | None |

Phases 1–4 are independent and can be executed in any order.
Phase 5 depends on all of 1–4.
Phase 6 depends on 5.

---

## Phase 1: Merge References (3 → 1)

### Task 1.1: Refactor ReferencesService

**File:** `src/SharpMCP/Services/ReferencesService.cs` (174 LOC)

1. **Change `FindReferencesAsync` signature** — add `string mode = "all"` parameter:
   ```csharp
   public async Task<List<ReferenceResult>> FindReferencesAsync(
       string solutionPath, string symbolName, string? typeName = null,
       string? projectScope = null, DetailLevel detail = DetailLevel.Compact,
       string mode = "all")
   ```

2. **Add mode dispatch** at top of method body:
   - `"callers"` → validate symbol is IMethodSymbol, then use existing `FindCallersAsync` logic (SymbolFinder.FindCallersAsync path)
   - `"usages"` / `"all"` → use existing `FindReferencesForSymbolAsync` path (SymbolFinder.FindReferencesAsync)

3. **Inline `FindCallersAsync` logic** into the `"callers"` branch:
   - Move lines 92–143 (the callers path) into a private `FindCallersForMethodAsync` helper
   - This helper takes `(IMethodSymbol method, Solution solution, string solutionDir, DetailLevel detail)` and returns `List<ReferenceResult>`
   - Eliminate the duplicate formatting code — reuse `GetLineText` and `GetContextLines` (already shared)

4. **Remove public methods:**
   - Delete `FindCallersAsync` (lines 92–143)
   - Delete `FindUsagesAsync` (lines 145–150)

5. **Update header format** based on mode:
   - The header is applied in the tool layer (ReferenceTools.cs), not here

### Task 1.2: Refactor ReferenceTools

**File:** `src/SharpMCP/Tools/ReferenceTools.cs` (95 LOC)

1. **Rewrite `FindReferences` tool method** — add `mode` parameter:
   ```csharp
   [McpServerTool(Name = "find_references"), Description("Find all references to a symbol across the solution. Use mode='callers' for method call sites only, or mode='usages' for type usage sites.")]
   public async Task<string> FindReferences(
       [Description("Path to .sln or .csproj file")] string solutionPath,
       [Description("Symbol name to find references for")] string symbolName,
       [Description("Containing type for disambiguation")] string? typeName = null,
       [Description("'all' (default), 'callers' (method calls only), or 'usages' (type usage sites)")] string mode = "all",
       [Description("Restrict search to a specific project")] string? projectScope = null,
       [Description("'compact' (default) or 'full' (includes context lines)")] string detail = "compact")
   ```

2. **Update header** based on mode:
   - `"all"` → `$"References to {symbolName}"`
   - `"callers"` → `$"Callers of {symbolName}"`
   - `"usages"` → `$"Usages of {symbolName}"`

3. **Delete `FindCallers` and `FindUsages` methods** entirely

**Checkpoint:** Build should compile. Reference tool count: 1 (was 3).

---

## Phase 2: Merge Hierarchy (2 → 1)

### Task 2.1: Refactor HierarchyService

**File:** `src/SharpMCP/Services/HierarchyService.cs` (122 LOC)

1. **Add new method `FindDerivedTypesAsync`:**
   ```csharp
   public async Task<List<SymbolResult>> FindDerivedTypesAsync(
       string solutionPath, string typeName, DetailLevel detail = DetailLevel.Compact)
   ```

2. **Implementation:**
   - Resolve type via `_symbolResolver.ResolveTypeAsync(solutionPath, typeName)`
   - Check `resolvedType.TypeKind`:
     - `TypeKind.Interface` → `SymbolFinder.FindImplementationsAsync(resolvedType, solution)`
     - `TypeKind.Class` → `SymbolFinder.FindDerivedClassesAsync(resolvedType, solution)`
     - Other → throw `ArgumentException("'{typeName}' is not an interface or class.")`
   - Filter: `!symbol.Locations.Any(l => l.IsInSource)` → skip metadata
   - Build results via `BuildSymbolResult(symbol, solutionDir)`
   - Return ordered by FilePath then Line

3. **Delete `FindImplementationsAsync` and `FindSubclassesAsync`**

### Task 2.2: Refactor HierarchyTools

**File:** `src/SharpMCP/Tools/HierarchyTools.cs` (104 LOC)

1. **Replace `FindImplementations` and `FindSubclasses`** with single tool:
   ```csharp
   [McpServerTool(Name = "find_derived_types"), Description("Find classes implementing an interface or inheriting from a base class. Automatically detects whether the type is an interface or class.")]
   public async Task<string> FindDerivedTypes(
       [Description("Path to .sln or .csproj file")] string solutionPath,
       [Description("Interface or base class name")] string typeName,
       [Description("'compact' (default) or 'full'")] string detail = "compact")
   ```

2. **Format header** based on service result (needs isInterface info):
   - Service can return the resolved type kind alongside results, OR
   - Simpler: just use neutral header `"Derived types of {typeName} ({count}):"`
   - Or: pass back the type kind as an out-of-band signal. Simplest: return a tuple or add a `kind` field to the response header in the tool layer.
   - **Decision:** Use neutral header: `"Types derived from {typeName} ({count}):"`

3. **Delete old `FindImplementations` and `FindSubclasses` methods**

**Checkpoint:** Build should compile. Hierarchy tool count: 3 (was 4).

---

## Phase 3: Fold Project Metadata (6 → 4)

### Task 3.1: Expand ProjectService.GetProjectInfoAsync

**File:** `src/SharpMCP/Services/ProjectService.cs` (163 LOC)

The current `GetProjectInfoAsync` returns a `Models.ProjectInfo` record which already has `ProjectReferences` (list of strings). Need to also include package references.

1. **Update `Models/ProjectInfo.cs`** — add PackageReferences field:
   ```csharp
   public record ProjectInfo(
       string Name, string FilePath, string TargetFramework,
       string OutputType, int SourceFileCount,
       IReadOnlyList<string> ProjectReferences,
       IReadOnlyList<PackageInfo> PackageReferences);  // NEW
   ```

2. **Update `BuildProjectInfo`** in ProjectService to also extract packages:
   - Reuse the `ListPackageReferencesAsync` parsing logic (XDocument PackageReference extraction)
   - Extract it into a static helper: `ParsePackageReferences(XDocument? doc)`
   - Call it from `BuildProjectInfo`

3. **Remove public methods:**
   - Delete `ListProjectReferencesAsync` (lines 54–62) — data now in ProjectInfo
   - Delete `ListPackageReferencesAsync` (lines 64–87) — data now in ProjectInfo

### Task 3.2: Update ProjectTools

**File:** `src/SharpMCP/Tools/ProjectTools.cs` (136 LOC)

1. **Update `GetProjectInfo` tool** — enhance formatting to include refs and packages:
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
   ```

2. **Delete `ListProjectReferences` and `ListPackageReferences` tool methods**

### Task 3.3: Update SymbolFormatter.FormatProjectList

**File:** `src/SharpMCP/Formatting/SymbolFormatter.cs`

The current `FormatProjectList` is used by `ListProjects` (multi-project summary). Need to check if `GetProjectInfo` uses it too and adjust formatting to include the new fields. If `GetProjectInfo` uses its own formatting in the tool layer, just update there.

**Checkpoint:** Build should compile. Project tool count: 4 (was 6). `ProjectInfo` model updated.

---

## Phase 4: Merge Symbol Search (2 → 1) + Bug Fix

### Task 4.1: Add `exact` mode to SymbolSearchService.FindSymbolsAsync

**File:** `src/SharpMCP/Services/SymbolSearchService.cs` (180 LOC)

1. **Change signature:**
   ```csharp
   public async Task<List<SymbolResult>> FindSymbolsAsync(
       string solutionPath, string query, string? kind = null,
       bool exact = false, DetailLevel detail = DetailLevel.Compact)
   ```

2. **Add `exact` branch:**
   - When `exact=true`: use `compilation.GetSymbolsWithName(query, SymbolFilter.All)` (exact match)
   - Filter to `IsInSource` (fixes the bug for both modes)
   - When `detail == Full`: extract doc comment + source body (current `GetSymbolInfoAsync` logic)
   - Return first match as single-element list

3. **Fix bug: add `IsInSource` filter** to the `exact=false` path too:
   - Add `if (!symbol.Locations.Any(l => l.IsInSource)) continue;` to the existing loop

4. **Delete `GetSymbolInfoAsync`** (lines 124–153)

### Task 4.2: Update SymbolTools

**File:** `src/SharpMCP/Tools/SymbolTools.cs` (129 LOC)

1. **Add `exact` parameter** to `FindSymbol`:
   ```csharp
   [McpServerTool(Name = "find_symbol"), Description("Search for symbols by name. Use exact=true for precise lookup with detailed info, or substring matching (default) for broader search.")]
   public async Task<string> FindSymbol(
       [Description("Path to .sln or .csproj file")] string solutionPath,
       [Description("Symbol name or substring")] string query,
       [Description("Filter: class, interface, method, property, field, enum, struct, event")] string? kind = null,
       [Description("true = exact name match, false = substring match (default)")] bool exact = false,
       [Description("'compact' (default) or 'full' (includes source + docs)")] string detail = "compact")
   ```

2. **Pass `exact` and `detail`** through to service:
   ```csharp
   var results = await _symbolService.FindSymbolsAsync(solutionPath, query, kind, exact, detailLevel);
   ```

3. **Delete `GetSymbolInfo` method** entirely

**Checkpoint:** Build should compile. Symbol tool count: 4 (was 5). Metadata symbol bug fixed.

---

## Phase 5: Build + Verify

### Task 5.1: Build

```bash
dotnet build src/SharpMCP
```

Target: 0 warnings, 0 errors.

### Task 5.2: Verify tool count

Count `[McpServerTool]` attributes across all tool files. Expected: **20**.

```
ProjectTools:    4 (list_projects, get_project_info, list_source_files, get_diagnostics)
SymbolTools:     4 (find_symbol, get_file_symbols, get_type_members, list_namespaces)
HierarchyTools:  3 (find_derived_types, get_type_hierarchy, find_overrides)
ReferenceTools:  1 (find_references)
SourceTools:     2 (get_source, get_file_content)
RefactoringTools:4 (rename_symbol, extract_interface, implement_interface, change_signature)
AnalysisTools:   2 (find_unused_code, find_code_smells)
Total:          20
```

---

## Phase 6: Update Documentation

### Task 6.1: Update README.md

- Tool count: 26 → 20
- Update all tool tables to reflect new names/parameters
- Remove entries for deleted tools
- Add new entries (find_derived_types, find_references with mode, find_symbol with exact)

### Task 6.2: Update PROJECT_INDEX.md

- Tool counts per class
- Total tool count
- Service method changes
- LOC updates

### Task 6.3: Update docs/API_REFERENCE.md

- Tool count in header
- Remove deleted tool sections (7 sections removed)
- Update merged tool sections with new parameters
- Add find_derived_types section

### Task 6.4: Update docs/SERVICES.md

- ReferencesService: updated method signatures, mode dispatch
- HierarchyService: merged method, auto-detection
- ProjectService: expanded GetProjectInfoAsync
- SymbolSearchService: exact mode, IsInSource fix

### Task 6.5: Update docs/ARCHITECTURE.md

- Tool counts in system diagram
- Updated tool reference table

---

## Dependency Graph

```
Phase 1 (References)  ──┐
Phase 2 (Hierarchy)   ──┤
Phase 3 (Project)     ──├── Phase 5 (Build) ── Phase 6 (Docs)
Phase 4 (Symbols)     ──┘
```

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| `ProjectInfo` record change breaks existing callers | Only used in ProjectTools.GetProjectInfo and ProjectTools.ListProjects — both get updated |
| `find_symbol` IsInSource filter removes valid results | Filter matches behavior of every other symbol-returning tool — this is a bug fix |
| Renamed tool breaks MCP clients | Clean break per user decision — no aliases |
| `find_references` mode=callers on non-method symbol | Validate symbol kind and return clear error message |

## Estimated Scope

| Phase | Lines removed | Lines added | Net |
|-------|--------------|-------------|-----|
| 1 (References) | ~80 | ~30 | −50 |
| 2 (Hierarchy) | ~50 | ~30 | −20 |
| 3 (Project) | ~45 | ~20 | −25 |
| 4 (Symbols) | ~40 | ~20 | −20 |
| 5 (Build) | 0 | 0 | 0 |
| 6 (Docs) | ~60 | ~50 | −10 |
| **Total** | **~275** | **~150** | **−125** |
