# SharpMCP Service Internals

Detailed documentation of each service's implementation, Roslyn API usage, and behavioral nuances.

## WorkspaceManager

**File:** `Services/WorkspaceManager.cs` (192 LOC)
**Dependencies:** None
**State:** `Dictionary<string, CachedWorkspace> _cache`, `SemaphoreSlim _lock(1,1)`

### Lifecycle

```
GetSolutionAsync(path)
  │
  ├─ Cache hit + fresh? → return cached Solution
  │
  ├─ Cache hit + stale? → Dispose old workspace → reload
  │
  └─ Cache miss → Create MSBuildWorkspace → Open → Cache → return
```

### Staleness Detection

- 5-second `StaleCheckInterval` gates filesystem checks
- Enumerates all `*.cs` files recursively under solution directory
- Compares `File.GetLastWriteTimeUtc` against `CachedWorkspace.LoadedAt`
- Any newer file → workspace is stale → dispose and reload

### Write Path

```csharp
ApplyChangesAsync(solutionPath, newSolution)
  → workspace.TryApplyChanges(newSolution)  // writes to disk
  → cache[path].Solution = workspace.CurrentSolution  // update cache
```

### Key Details

- Supports both `.sln` and `.csproj` entry points (auto-wraps single project)
- `FindProject` falls back to `solution.Projects.FirstOrDefault()` when project name is null
- `RegisterWorkspaceFailedHandler(Action<WorkspaceDiagnosticEventArgs>)` — Roslyn 5.0 API (not obsolete event)
- Disposes previous workspace before reloading to prevent resource leaks

---

## SymbolResolver

**File:** `Services/SymbolResolver.cs` (145 LOC)
**Dependencies:** WorkspaceManager
**State:** None (stateless)

### Resolution Strategy

```
ResolveTypeAsync(path, "MyClass")
  │
  ├─ Get compilation for each project
  ├─ GetAllNamedTypes(compilation) — recursive namespace traversal
  ├─ Match on type.Name == "MyClass" OR type.ToDisplayString() == "MyClass"
  │
  ├─ 0 matches → throw "Type 'MyClass' not found"
  ├─ 1 match  → return
  └─ N matches → throw with candidate list
```

```
ResolveSymbolAsync(path, "DoWork", containingType: "MyService")
  │
  ├─ containingType provided? → resolve type, search type.GetMembers()
  └─ no containingType → compilation.GetSymbolsWithName("DoWork")
```

```
ResolveMethodAsync(path, "DoWork", typeName?)
  │
  ├─ ResolveSymbolAsync → filter to IMethodSymbol
  ├─ 0 methods → throw
  ├─ 1 method  → return
  └─ N methods → return first + stderr warning (overload ambiguity)
```

### `GetAllNamedTypes` (static)

Recursive traversal: global namespace → all namespace members → all type members → nested types. Used by `SymbolSearchService` and `AnalysisService`.

---

## SymbolSearchService

**File:** `Services/SymbolSearchService.cs` (179 LOC)
**Dependencies:** WorkspaceManager, SymbolResolver
**State:** None

### Find Symbols

```
FindSymbolsAsync(path, "Manager", kind: "class")
  │
  ├─ compilation.GetSymbolsWithName(s => s.Contains("Manager", OrdinalIgnoreCase))
  ├─ Filter: kind match, not IsImplicitlyDeclared
  ├─ Deduplicate: HashSet<"displayString|kind"> across projects
  └─ Return List<SymbolResult>
```

### File Symbols

```
GetFileSymbolsAsync(path, "Services/Foo.cs", depth: 1)
  │
  ├─ Resolve relative path against solution directory
  ├─ Find matching Document across all projects (case-insensitive FilePath)
  ├─ GetSyntaxRoot → DescendantNodes<BaseTypeDeclarationSyntax>
  ├─ semanticModel.GetDeclaredSymbol(node) → cast to INamedTypeSymbol
  ├─ depth > 0 → expand to type.GetMembers() (skip compiler-generated)
  └─ Return List<SymbolResult>
```

### Namespace Listing

Traverses `GetAllNamedTypes`, collects `type.ContainingNamespace.ToDisplayString()`. Filters to types with `IsInSource` locations only.

---

## ProjectService

**File:** `Services/ProjectService.cs` (162 LOC)
**Dependencies:** WorkspaceManager
**State:** None

### Project Info Extraction

Roslyn provides project name, documents, and references. For framework and output type, the service parses the `.csproj` XML directly via `XDocument.Load`:

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>  <!-- or TargetFrameworks -->
  <OutputType>Exe</OutputType>                <!-- defaults to "Library" -->
</PropertyGroup>
```

### Package References

Also parsed from `.csproj` XML:
```xml
<PackageReference Include="Foo" Version="1.0" />
<!-- or -->
<PackageReference Include="Foo"><Version>1.0</Version></PackageReference>
```

### Diagnostics

```
GetDiagnosticsAsync(path, projectName?)
  │
  ├─ Get compilation(s) — all projects if projectName is null
  ├─ compilation.GetDiagnostics()
  ├─ Filter: Severity >= Warning
  ├─ Sort: errors first, then by file, then by line
  └─ Return List<DiagnosticInfo>
```

---

## SourceService

**File:** `Services/SourceService.cs` (59 LOC)
**Dependencies:** SymbolResolver
**State:** None

### Symbol Source

```
GetSymbolSourceAsync(path, "DoWork", typeName?)
  │
  ├─ SymbolResolver.ResolveSymbolAsync(...)
  ├─ symbol.DeclaringSyntaxReferences.FirstOrDefault()
  ├─ syntaxRef.GetSyntaxAsync() → node
  ├─ node.GetLocation().GetLineSpan() → file + line
  └─ Return "{path}:{line}\n{node.ToFullString().TrimEnd()}"
```

Throws if `DeclaringSyntaxReferences` is empty (metadata-only symbol).

### File Content (static)

```
GetFileContentAsync(filePath, startLine?, endLine?)
  │
  ├─ File size guard: > 5 MB → throw
  ├─ Read all lines
  ├─ Clamp startLine/endLine to valid range
  └─ Return "  42 | code here" format
```

---

## HierarchyService

**File:** `Services/HierarchyService.cs` (121 LOC)
**Dependencies:** WorkspaceManager, SymbolResolver
**State:** None

All hierarchy queries follow the same pattern:

```
1. Resolve input type/method via SymbolResolver
2. Validate kind (interface for implementations, class for subclasses, etc.)
3. Call SymbolFinder.FindXxxAsync(symbol, solution)
4. Filter results to IsInSource only
5. Build SymbolResult list
```

### Type Hierarchy

```
GetTypeHierarchyAsync(path, "OrderService")
  │
  ├─ Walk type.BaseType chain until System.Object
  ├─ Append "object" explicitly
  ├─ Collect type.AllInterfaces (transitive)
  └─ Return TypeHierarchyResult
```

---

## ReferencesService

**File:** `Services/ReferencesService.cs` (173 LOC)
**Dependencies:** WorkspaceManager, SymbolResolver
**State:** None

### Shared Implementation

`FindReferencesForSymbolAsync` is used by both `FindReferencesAsync` and `FindUsagesAsync`:

```
SymbolFinder.FindReferencesAsync(symbol, solution)
  │
  ├─ For each ReferencedSymbol.Locations:
  │   ├─ Get document, source text, line text
  │   ├─ GetEnclosingSymbol → ContainingSymbol
  │   ├─ Full detail? → GetContextLines(±2 lines)
  │   └─ Build ReferenceResult
  │
  ├─ projectScope filter (if provided)
  └─ Return List<ReferenceResult>
```

### Callers

```
FindCallersAsync → SymbolFinder.FindCallersAsync(method, solution)
  │
  └─ Uses caller.CallingSymbol for ContainingSymbol (instead of GetEnclosingSymbol)
```

---

## RenameService

**File:** `Services/RenameService.cs` (181 LOC)
**Dependencies:** WorkspaceManager, SymbolResolver
**State:** None (but invalidates cache after rename)

### Rename Flow

```
RenameSymbolAsync(path, "OldName", "NewName", typeName?, includeStrings)
  │
  ├─ Validate name: ^@?[\p{L}_][\p{L}\p{Nd}_]*$
  ├─ Validate kind: NamedType | Method | Property | Field | Event
  ├─ Validate location: must be IsInSource
  │
  ├─ Renamer.RenameSymbolAsync(solution, symbol, options, newName)
  │     options: RenameOverloads=false, RenameInStrings=includeStrings,
  │              RenameInComments=includeStrings, RenameFile=false
  │
  ├─ workspace.TryApplyChanges(newSolution)
  │
  ├─ If INamedTypeSymbol AND filename matches old name:
  │     File.Move(oldPath, newPath)
  │
  ├─ InvalidateCacheAsync
  │
  └─ Return summary: changed files list + rename annotation
```

---

## InterfaceService

**File:** `Services/InterfaceService.cs` (345 LOC)
**Dependencies:** WorkspaceManager, SymbolResolver
**State:** None (invalidates cache after writes)

### Extract Interface

```
ExtractInterfaceAsync(path, "MyService", interfaceName?, apply)
  │
  ├─ Resolve class → collect public non-static members:
  │     Methods (Ordinary only), Properties, Events
  │     Exclude: constructors, operators, finalizers
  │
  ├─ Generate interface code (StringBuilder):
  │     namespace, interface declaration, member signatures
  │     Handles: generics, ref/out/in/params, init setters
  │
  ├─ apply=false? → return preview
  │
  ├─ apply=true:
  │     Write I{TypeName}.cs to same directory
  │     Add ": I{TypeName}" to class base list (Roslyn AST rewrite)
  │     InvalidateCache
  │
  └─ Return generated code + change summary
```

### Implement Interface

```
ImplementInterfaceAsync(path, "MyService", interfaceName?)
  │
  ├─ Resolve class → get declared interfaces (or specific one)
  ├─ For each interface member:
  │     Check FindImplementationForInterfaceMember → already implemented?
  │     If not: generate stub with throw new NotImplementedException()
  │
  ├─ Insert stubs via text insertion at closing brace position
  ├─ InvalidateCache
  └─ Return list of added members
```

---

## AnalysisService

**File:** `Services/AnalysisService.cs` (431 LOC)
**Dependencies:** WorkspaceManager
**State:** None

### Unused Code Detection

```
FindUnusedCodeAsync(path, scope, projectName?)
  │
  ├─ Get compilations (filter to project if specified)
  ├─ For each compilation:
  │     GetAllNamedTypes → for each type:
  │       ├─ ShouldCheckType? → IsUnreferencedAsync?
  │       └─ For each member:
  │             ShouldCheckMethod/Property/Field? → IsUnreferencedAsync?
  │
  └─ Return grouped output: Types / Methods / Properties / Fields
```

### Exclusion Rules

| Rule | Applies to | Rationale |
|------|-----------|-----------|
| Public types | Types | May be consumed externally |
| `Program` / `<Program>$` | Types | Entry point |
| `Main` method | Methods | Entry point |
| Non-Ordinary methods | Methods | Constructors, operators, etc. |
| Interface implementations | Methods | Called via interface dispatch |
| Override methods | Methods | Called via base class dispatch |
| Public const fields | Fields | Used as attribute parameters |
| Excluded attributes | All | Test frameworks, MCP, ASP.NET, serialization, [Obsolete] |

### `IsUnreferencedAsync`

```csharp
SymbolFinder.FindReferencesAsync(symbol, solution)
  → All reference groups have zero locations? → unreferenced
```

### Code Smell Detection

```
FindCodeSmellsAsync(path, category, projectName?, deep)
  │
  ├─ Get compilations (filter to project if specified)
  ├─ For each compilation, run checks by category:
  │     complexity → CheckMethodBodySmellsAsync + CheckLargeClasses + CheckLongParameterLists
  │     design    → CheckMethodBodySmellsAsync + CheckGodClasses + CheckDataClasses
  │                 + CheckTooManyDependencies + (deep? CheckFeatureEnvyAsync)
  │     inheritance → CheckDeepInheritance + CheckRefusedBequest + CheckSpeculativeGenerality
  │
  ├─ CheckMethodBodySmellsAsync runs once, results filtered by active category
  │
  └─ FormatSmellResults: group by severity (critical → warning → info),
       sub-group by smell name with threshold in header
```

`FormatSmellHeader` annotates each smell group with its threshold (e.g., `"Long method (>100 lines)"`).

`HasExcludedAttribute` and `ExcludedAttributes` are `internal static` — shared with `CodeSmellChecks`.

---

## CodeSmellChecks

**File:** `Services/CodeSmellChecks.cs` (533 LOC)
**Dependencies:** None (static utility class, not DI-registered)
**State:** None

Static class containing all 13 code smell detection algorithms. Used by `AnalysisService.FindCodeSmellsAsync`.

### Internal Records

```csharp
SmellResult(SmellName, Severity, SymbolName, Detail, FilePath, Line)
MethodBodyMetrics(LineCount, MaxNestingDepth, CyclomaticComplexity, IsSingleDelegation)
```

### Single-Pass Method Body Analysis

`AnalyzeMethodBodyAsync(IMethodSymbol)` calls `DeclaringSyntaxReferences[0].GetSyntaxAsync()` once per method and extracts all four metrics:

| Metric | Algorithm |
|--------|-----------|
| `LineCount` | `body.GetText().Lines.Count` (1 for expression body) |
| `MaxNestingDepth` | Recursive `ComputeMaxNesting` — increments depth for `if`, `for`, `foreach`, `while`, `do`, `switch`, `try` |
| `CyclomaticComplexity` | Counts `DescendantNodes`: `if`, `case`, `case pattern`, `switch arm`, `?:`, `??`, `&&`, `||`, `catch`. Base = 1 |
| `IsSingleDelegation` | Body has exactly 1 statement that is an `ExpressionStatement(Invocation)` or `ReturnStatement(Invocation)` |

`CheckMethodBodySmellsAsync` iterates all types → all `MethodKind.Ordinary` methods → calls `AnalyzeMethodBodyAsync` once per method → evaluates 4 smells from the same metrics (long method, deep nesting, high complexity, middle man).

### Symbol-Level Checks (Synchronous)

| Method | Smell | Key Logic |
|--------|-------|-----------|
| `CheckLargeClasses` | Large class | `GetMembers()` count excl. implicit + nested types |
| `CheckLongParameterLists` | Long param list | `Parameters.Length` for Ordinary + Constructor methods |
| `CheckGodClasses` | God class | members >20 AND distinct field/property types >5 |
| `CheckDataClasses` | Data class | 0 ordinary methods + ≥2 properties. Skips records |
| `CheckTooManyDependencies` | Too many deps | Max constructor `Parameters.Length`. Skips static classes |
| `CheckDeepInheritance` | Deep hierarchy | Walk `BaseType` chain (excl. object) |
| `CheckRefusedBequest` | Refused bequest | Base virtual/abstract ≥3 AND override rate <20% |
| `CheckSpeculativeGenerality` | Speculative generality | Unused type parameter on type or method |

### Semantic Check (deep=true only)

`CheckFeatureEnvyAsync` walks `MemberAccessExpressionSyntax` in each method body → `GetSymbolInfo` → groups accesses by `ContainingType`. If max external type accesses > own type accesses (min 3 external) → warning.

### Roslyn Syntax Notes

- `??` is `BinaryExpressionSyntax` with `SyntaxKind.CoalesceExpression` (no separate `CoalesceExpressionSyntax`)
- `SwitchExpressionArmSyntax` for C# 8+ switch expressions (separate from `CaseSwitchLabelSyntax`)
- Expression-bodied members: `Body` is null → check `ExpressionBody`, count as 1 line

---

## SignatureService

**File:** `Services/SignatureService.cs` (415 LOC)
**Dependencies:** WorkspaceManager, SymbolResolver
**State:** None (invalidates cache after writes)

### Change Signature Flow

```
ChangeSignatureAsync(path, method, type?, add?, remove?, reorder?)
  │
  ├─ Resolve method
  ├─ Parse changes: addParameters, removeParameters, reorderParameters
  ├─ Compute new parameter list
  │
  ├─ Find all call sites: SymbolFinder.FindCallersAsync
  │     Locate InvocationExpressionSyntax at each caller location
  │
  ├─ Modify method declaration (text-based):
  │     Splice new parameter list into source text
  │     Write modified file to disk
  │
  ├─ Modify call sites (text-based, per file):
  │     Sort by descending position (avoid index shift)
  │     For same-file-as-declaration: re-read from disk
  │     Splice new argument list into source text
  │     Write each modified file
  │
  ├─ InvalidateCache
  │
  └─ Return: before/after signature + call site count
```

### Parameter Parsing

- `SplitParameters`: Custom comma-splitter respecting `<>` nesting depth
- `ParseAddParameters`: `"type name"` or `"type name=default"` — type is everything before last space token
- Named arguments at call sites detected via `arg.NameColon.Name.Identifier.ValueText`
- `FormatDefaultValue`: Pattern matches null → `"null"`, string → `"\"...\""`, bool → lowercase, others → `.ToString()`

### Call Site Update Rules

| Change | At call site |
|--------|-------------|
| Add param with default | No change (default kicks in) |
| Add param without default | Insert `default(T)` |
| Remove param | Remove corresponding argument |
| Reorder params | Reorder arguments (respects named args) |
