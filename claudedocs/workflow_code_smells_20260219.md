# Workflow: Implement `find_code_smells` Tool

**Source**: Plan at `.claude/plans/inherited-baking-dusk.md`
**Requirements**: `claudedocs/requirements_code_smells_20260219.md`

---

## Phase 1: Foundation — Internal Models + Shared Helpers
**Goal**: Establish the data types and utility functions everything else depends on.

### Task 1.1: Create `Services/CodeSmellChecks.cs` scaffold
- Create file with namespace, usings, internal records
- `SmellResult(SmellName, Severity, SymbolName, Detail, FilePath, Line)`
- `MethodBodyMetrics(LineCount, MaxNestingDepth, CyclomaticComplexity, IsSingleDelegation)`
- Empty `internal static class CodeSmellChecks { }`

### Task 1.2: Implement shared helpers
- `ShouldAnalyzeType(INamedTypeSymbol)` — in-source, not implicit, not enum/delegate/interface, not `Program`/`<Program>$`, not excluded attribute
- `GetLocation(ISymbol, string solutionDir)` — extract file path + line using `LocationFormatter.MakePathRelative`
- `MakeResult(string smellName, string severity, ISymbol symbol, string detail, string solutionDir)` — factory that calls `GetLocation`

### Task 1.3: Change visibility in `AnalysisService.cs`
- `HasExcludedAttribute`: `private static` → `internal static`
- `ExcludedAttributes`: `private static readonly` → `internal static readonly`

**Checkpoint**: `dotnet build src/SharpMCP` — 0 errors

---

## Phase 2: Method Body Analysis Engine
**Goal**: Build the syntax-walking infrastructure that powers 4 smell checks simultaneously.

### Task 2.1: Implement `AnalyzeMethodBodyAsync(IMethodSymbol)`
- Get syntax node via `DeclaringSyntaxReferences[0].GetSyntaxAsync()`
- Handle `MethodDeclarationSyntax` — extract `Body` or `ExpressionBody`
- Handle `LocalFunctionStatementSyntax` and `AccessorDeclarationSyntax` gracefully (return null)
- Compute all 4 metrics in single pass, return `MethodBodyMetrics`

### Task 2.2: Implement `ComputeMaxNesting(SyntaxNode)`
- Recursive walk of `ChildNodes()`
- Increment depth for: `IfStatementSyntax`, `ForStatementSyntax`, `ForEachStatementSyntax`, `WhileStatementSyntax`, `DoStatementSyntax`, `SwitchStatementSyntax`, `TryStatementSyntax`
- Return max depth across all branches

### Task 2.3: Implement `ComputeCyclomaticComplexity(SyntaxNode)`
- Walk `DescendantNodes()`, base complexity = 1
- Count: `IfStatementSyntax`, `CaseSwitchLabelSyntax`, `CasePatternSwitchLabelSyntax`, `SwitchExpressionArmSyntax`, `ConditionalExpressionSyntax`, `CoalesceExpressionSyntax`, `CatchClauseSyntax`
- Count: `BinaryExpressionSyntax` where kind is `LogicalAndExpression` or `LogicalOrExpression`

### Task 2.4: Implement `IsSingleDelegation` detection
- Check: body has exactly 1 statement
- That statement is `ExpressionStatementSyntax` with `InvocationExpressionSyntax` child, OR `ReturnStatementSyntax` with `InvocationExpressionSyntax` expression

### Task 2.5: Implement `CheckMethodBodySmellsAsync(Compilation, solutionDir)`
- Iterate all types via `SymbolResolver.GetAllNamedTypes(compilation)`
- Filter with `ShouldAnalyzeType`
- For each `IMethodSymbol` (MethodKind.Ordinary, not implicit):
  - Call `AnalyzeMethodBodyAsync` once
  - Evaluate long method thresholds (>50 warn, >100 crit)
  - Evaluate deep nesting thresholds (>3 warn, >5 crit)
  - Evaluate cyclomatic complexity thresholds (>10 warn, >20 crit)
  - Track delegation count per type for middle man
- After all methods of a type: evaluate middle man (>80% delegation, ≥3 methods = warn)

**Checkpoint**: `dotnet build src/SharpMCP` — 0 errors

---

## Phase 3: Symbol-Level Smell Checks
**Goal**: Implement the 8 checks that operate on symbol metadata only (no syntax walking).

### Task 3.1: Complexity — `CheckLargeClasses`
- `type.GetMembers().Count(m => !m.IsImplicitlyDeclared && m is not INamedTypeSymbol)`
- >20 members = warning, >40 = critical

### Task 3.2: Complexity — `CheckLongParameterLists`
- Iterate methods (`MethodKind.Ordinary` and `MethodKind.Constructor`)
- `method.Parameters.Length` — >5 = warning, >8 = critical

### Task 3.3: Design — `CheckGodClasses`
- Requires memberCount > 20 AND dependencyCount > 5
- Dependencies = distinct non-primitive types of fields + properties (excl. `SpecialType.None` check, excl. self)
- Skip static classes (`type.IsStatic`)

### Task 3.4: Design — `CheckDataClasses`
- 0 ordinary methods + ≥2 properties = info
- Skip records (`type.IsRecord`), skip static classes

### Task 3.5: Design — `CheckTooManyDependencies`
- Max constructor `Parameters.Length`
- >5 = warning, >8 = critical
- Skip static classes

### Task 3.6: Inheritance — `CheckDeepInheritance`
- Walk `type.BaseType` chain, count depth (stop at `SpecialType.System_Object`)
- >3 levels = warning

### Task 3.7: Inheritance — `CheckRefusedBequest`
- Count base virtual/abstract members (non-implicit)
- Count derived override members
- Rate < 20% AND base overridable count ≥ 3 = info

### Task 3.8: Inheritance — `CheckSpeculativeGenerality`
- For each `TypeParameter` on type or method
- Check if parameter name appears in any member signature (`ToDisplayString()`)
- Unused = info

**Checkpoint**: `dotnet build src/SharpMCP` — 0 errors

---

## Phase 4: Deep Analysis — Feature Envy
**Goal**: Implement the expensive semantic check gated behind `deep=true`.

### Task 4.1: Implement `CheckFeatureEnvyAsync(Compilation, Solution, Project, solutionDir)`
- Iterate `project.Documents`
- For each document: `GetSemanticModelAsync()` + `GetSyntaxRootAsync()`
- Find all `MethodDeclarationSyntax` in root
- For each method body: walk `MemberAccessExpressionSyntax` nodes
- Resolve via `semanticModel.GetSymbolInfo(access).Symbol`
- Group by `ContainingType` — count own vs external accesses
- If max external > own AND max external ≥ 3 = warning

**Checkpoint**: `dotnet build src/SharpMCP` — 0 errors

---

## Phase 5: Orchestration + Output Formatting
**Goal**: Wire everything together in `AnalysisService` and format the output.

### Task 5.1: Add `FormatSmellResults` to `AnalysisService.cs`
- Group results by severity (critical → warning → info)
- Sub-group by smell name within each severity
- Format headers with threshold context via `FormatSmellHeader`
- Each entry: `    SymbolName (Detail)  [FilePath:Line]`
- Empty case: `"No code smells found in {name} (category: {category})."`

### Task 5.2: Add `FindCodeSmellsAsync` to `AnalysisService.cs`
- Get solution + solutionDir (reuse `FindUnusedCodeAsync` pattern)
- Filter projects by `projectName`
- For each project → get compilation
- Category-based dispatch:
  - complexity OR design active → call `CheckMethodBodySmellsAsync`, filter results by smell name
  - complexity active → `CheckLargeClasses`, `CheckLongParameterLists`
  - design active → `CheckGodClasses`, `CheckDataClasses`, `CheckTooManyDependencies`
  - design AND deep → `CheckFeatureEnvyAsync`
  - inheritance active → `CheckDeepInheritance`, `CheckRefusedBequest`, `CheckSpeculativeGenerality`
- Collect all results → `FormatSmellResults`

### Task 5.3: Add tool method to `AnalysisTools.cs`
- `[McpServerTool(Name = "find_code_smells")]` with `Description`
- Parameters: solutionPath, category, projectName, deep
- Thin wrapper: try/catch → delegate to `_analysisService.FindCodeSmellsAsync`

**Checkpoint**: `dotnet build src/SharpMCP` — 0 errors, 0 warnings

---

## Phase 6: Verification
**Goal**: Validate the tool works correctly on a real codebase.

### Task 6.1: Build verification
- `dotnet build src/SharpMCP` — must be 0 warnings, 0 errors

### Task 6.2: Self-test — run against SharpMCP's own codebase
- Call `find_code_smells` with default params → verify severity-grouped output
- Call with `category="complexity"` → verify only complexity smells returned
- Call with `category="design"` → verify only design smells returned
- Call with `category="inheritance"` → verify only inheritance smells returned
- Call with `deep=true` → verify feature envy results appear (or "none" if no envy detected)

### Task 6.3: Threshold sanity check
Expected results on SharpMCP itself:
- `SignatureService.ChangeSignatureAsync` should flag as long method (it's the largest method)
- `InterfaceService.ExtractInterfaceAsync` should flag as long method
- Tool classes with many DI params should flag as too many dependencies
- Data records like `PackageInfo`, `DiagnosticInfo` should flag as data class (but NOT `SymbolResult` etc. since they're records)
- No false positives on entry points, MCP tool methods, or compiler-generated code

---

## Dependency Graph

```
Phase 1 ──► Phase 2 ──► Phase 5
   │                       ▲
   └──────► Phase 3 ───────┘
   │                       ▲
   └──────► Phase 4 ───────┘
                           │
                      Phase 6
```

Phases 2, 3, 4 can be implemented in any order after Phase 1.
Phase 5 depends on all of 2, 3, 4.
Phase 6 depends on Phase 5.

---

## Files Modified (Summary)

| File | Phase | Action |
|------|-------|--------|
| `src/SharpMCP/Services/CodeSmellChecks.cs` | 1-4 | CREATE (~450 LOC) |
| `src/SharpMCP/Services/AnalysisService.cs` | 1, 5 | MODIFY (+100 LOC, visibility change + orchestration + formatting) |
| `src/SharpMCP/Tools/AnalysisTools.cs` | 5 | MODIFY (+20 LOC, tool method) |

No changes to: `Program.cs`, `Models/`, `Formatting/`, other services.
