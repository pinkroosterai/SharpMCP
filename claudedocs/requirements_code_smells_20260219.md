# Requirements: `find_code_smells` Tool

## Goal

Detect common code smells across a solution using Roslyn syntax and semantic analysis. Returns a severity-ranked list of issues grouped by critical/warning/info.

---

## Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `category` | string | no | `"all"` | Filter: `"all"`, `"complexity"`, `"design"`, `"inheritance"` |
| `projectName` | string | no | null | Restrict analysis to a specific project |
| `deep` | bool | no | `false` | Enable expensive checks (feature envy). Default off for performance. |

---

## Code Smells Detected

### Category: `complexity` — Size & Complexity Metrics

| Smell | Detection Method | Threshold | Severity |
|-------|-----------------|-----------|----------|
| **Long method** | Count lines in method body syntax node | >50 lines = warning, >100 = critical | warning/critical |
| **Large class** | Count non-implicit members on type | >20 members = warning, >40 = critical | warning/critical |
| **Long parameter list** | Count method parameters | >5 params = warning, >8 = critical | warning/critical |
| **Deep nesting** | Walk syntax tree, track nesting depth of if/for/foreach/while/switch/try | >3 levels = warning, >5 = critical | warning/critical |
| **High cyclomatic complexity** | Count branching nodes in method body (if, case, &&, \|\|, ?:, ??, catch) | >10 = warning, >20 = critical | warning/critical |

### Category: `design` — Design Smells

| Smell | Detection Method | Threshold | Severity |
|-------|-----------------|-----------|----------|
| **God class** | Class with >20 members AND >5 field/property dependencies | Both conditions met | critical |
| **Data class** | Class with only properties/fields, no methods (excluding compiler-generated) | 0 non-accessor methods, ≥2 properties | info |
| **Middle man** | Class where >80% of methods are single-line delegations (method body is a single invocation expression) | >80% of methods | warning |
| **Too many dependencies** | Constructor parameter count (DI indicator) | >5 params = warning, >8 = critical | warning/critical |
| **Feature envy** *(deep only)* | Method body accesses more members from a single external type than from its own containing type | External > own type accesses | warning |

### Category: `inheritance` — Inheritance Smells

| Smell | Detection Method | Threshold | Severity |
|-------|-----------------|-----------|----------|
| **Deep inheritance tree** | Walk BaseType chain, count depth | >3 levels (excluding object) | warning |
| **Refused bequest** | Subclass overrides <20% of virtual/abstract members from base | <20% override rate on class with ≥3 overridable members | info |
| **Speculative generality** | Type parameter on class/method that is never used in any member signature or body | Unused type param exists | info |

---

## Exclusions

Symbols excluded from analysis (consistent with `find_unused_code`):
- Compiler-generated types and members (`IsImplicitlyDeclared`, name starts with `<`)
- Metadata-only symbols (no `IsInSource` location)
- Types/methods with excluded attributes: `[McpServerTool]`, `[Fact]`, `[Test]`, `[ApiController]`, etc.
- `Program` / `<Program>$` entry point class
- Records and record structs for "data class" (they are intentionally data-centric)
- Enums, delegates, and interfaces (not applicable to most smells)

---

## Output Format

Results grouped by severity, then by smell type within each group:

```
Code smells in SharpMCP (12 issues):

Critical (3):
  Long method (>100 lines):
    ChangeSignatureAsync (142 lines)  [Services/SignatureService.cs:22]
  God class:
    SymbolFormatter (42 members, 8 dependencies)  [Formatting/SymbolFormatter.cs:5]
  High complexity (>20):
    FindUnusedCodeAsync (complexity: 24)  [Services/AnalysisService.cs:17]

Warning (6):
  Long method (>50 lines):
    RenameSymbolAsync (68 lines)  [Services/RenameService.cs:22]
    ExtractInterfaceAsync (55 lines)  [Services/InterfaceService.cs:22]
  Long parameter list (>5):
    FindReferencesAsync (5 params)  [Services/ReferencesService.cs:18]
  Too many dependencies (>5):
    RefactoringTools (6 constructor params)  [Tools/RefactoringTools.cs:8]
  Deep nesting (>3):
    IsInterfaceImplementation (4 levels)  [Services/AnalysisService.cs:171]

Info (3):
  Data class:
    PackageInfo  [Models/ProjectInfo.cs:12]
    DiagnosticInfo  [Models/ProjectInfo.cs:14]
  Speculative generality:
    (none)
```

When no smells found: `"No code smells found in SharpMCP (category: all)."`

---

## Architecture

### Service: `AnalysisService` (extend existing)

Add method:
```
Task<string> FindCodeSmellsAsync(string solutionPath, string category = "all", string? projectName = null, bool deep = false)
```

No new service needed — extend `AnalysisService` since it already handles solution-wide analysis. Keeps the DI graph unchanged.

### Tool: `AnalysisTools` (extend existing)

Add method:
```
[McpServerTool(Name = "find_code_smells")]
Task<string> FindCodeSmells(string solutionPath, string category = "all", string? projectName = null, bool deep = false)
```

### Internal Structure

Private helper methods in `AnalysisService`:

```
// Complexity category
CheckLongMethods(compilation, solution) → List<SmellResult>
CheckLargeClasses(compilation) → List<SmellResult>
CheckLongParameterLists(compilation) → List<SmellResult>
CheckDeepNesting(compilation) → List<SmellResult>
CheckCyclomaticComplexity(compilation) → List<SmellResult>

// Design category
CheckGodClasses(compilation) → List<SmellResult>
CheckDataClasses(compilation) → List<SmellResult>
CheckMiddleMan(compilation) → List<SmellResult>
CheckTooManyDependencies(compilation) → List<SmellResult>
CheckFeatureEnvy(compilation, solution) → List<SmellResult>  // deep only

// Inheritance category
CheckDeepInheritance(compilation) → List<SmellResult>
CheckRefusedBequest(compilation) → List<SmellResult>
CheckSpeculativeGenerality(compilation) → List<SmellResult>
```

### Internal Model

```csharp
private record SmellResult(
    string SmellName,       // "Long method"
    string Severity,        // "critical", "warning", "info"
    string SymbolName,      // "ChangeSignatureAsync"
    string Detail,          // "142 lines" or "8 dependencies"
    string FilePath,        // relative path
    int Line
);
```

No new public model needed — `SmellResult` is internal to `AnalysisService`. The tool returns a formatted string.

---

## Roslyn APIs Required

| Check | API |
|-------|-----|
| Method body lines | `methodDecl.Body.GetText().Lines.Count` or `ExpressionBody` span |
| Member count | `type.GetMembers().Where(!IsImplicitlyDeclared)` |
| Parameter count | `method.Parameters.Length` |
| Nesting depth | `SyntaxNode.DescendantNodes()` walking `IfStatement`, `ForStatement`, `WhileStatement`, `ForEachStatement`, `SwitchStatement`, `TryStatement` |
| Cyclomatic complexity | Count `IfStatement`, `CaseSwitchLabel`, `CasePatternSwitchLabel`, `ConditionalExpression`, `CoalesceExpression`, `BinaryExpression(&&, \|\|)`, `CatchClause` in method body |
| Constructor params | `type.Constructors.Max(c => c.Parameters.Length)` |
| Data class check | `type.GetMembers().OfType<IMethodSymbol>().Where(m.MethodKind == Ordinary)` count == 0 |
| Middle man | Parse method bodies: single `ExpressionStatement` containing `InvocationExpression` |
| Feature envy (deep) | `semanticModel.GetSymbolInfo(node)` for each `MemberAccessExpression` in method body, group by containing type |
| Inheritance depth | Walk `type.BaseType` chain |
| Refused bequest | `baseType.GetMembers().Where(IsVirtual/Abstract)` vs `type.GetMembers().Where(IsOverride)` |
| Speculative generality | `type.TypeParameters` / `method.TypeParameters` → check usage in member signatures via `ToDisplayString` |

---

## Performance Considerations

- **Fast path (deep=false):** All checks except feature envy use symbol-level or syntax-level analysis. No `FindReferencesAsync` calls needed. Should be fast even for large solutions.
- **Slow path (deep=true):** Feature envy requires walking every method body's member access expressions and resolving symbols. Proportional to total method body size in solution.
- **Early termination:** Each check can be independently skipped via `category` filter.

---

## User Stories

1. **As an LLM reviewing code**, I want to quickly identify the worst code smells in a solution so I can prioritize refactoring suggestions.
2. **As a developer**, I want to scan for complexity issues before they become maintenance problems.
3. **As a code reviewer**, I want to know which classes have too many dependencies (god class / too many DI params) to suggest architectural improvements.

## Acceptance Criteria

- [ ] `find_code_smells` tool registered and callable via MCP
- [ ] `category` filter works for "all", "complexity", "design", "inheritance"
- [ ] `projectName` filter restricts to named project
- [ ] `deep=true` enables feature envy detection
- [ ] Output sorted by severity (critical first)
- [ ] Exclusion rules match `find_unused_code` (no false positives on entry points, test methods, etc.)
- [ ] Thresholds produce reasonable results on SharpMCP's own codebase
- [ ] Builds with 0 warnings, 0 errors

## Open Questions

1. Should "magic numbers" (literal values in code) be detected? Easy to implement but very noisy.
2. Should "catch-all exception handlers" (`catch (Exception)`) be flagged? Relevant but overlaps with diagnostic analyzers.
3. Future: should there be a `--fix` or `--suggest` mode that recommends specific refactoring tools to apply? (e.g., "extract interface" for god class, "change signature" for long param list)
