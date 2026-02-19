# Requirements: New Tools Batch

## Overview
Four new tools spanning refactoring and analysis categories.
Priority order: extract_interface → implement_interface → find_unused_code → change_signature

---

## Tool 1: `extract_interface`

### Goal
Generate an interface from an existing class's public members and optionally apply it.

### Parameters
- `solutionPath` (string, required)
- `typeName` (string, required) — the class to extract from
- `interfaceName` (string, optional) — defaults to `I{typeName}`
- `apply` (bool, optional, default: true) — when true, creates the interface file and adds `: IFoo` to the class. When false, returns the generated interface code as preview.

### Behavior
1. Resolve the class via SymbolResolver
2. Collect all public non-static methods, properties, and events (excluding constructors, operators, finalizers)
3. Generate an interface declaration with matching member signatures
4. If `apply=true`:
   - Create a new file `I{typeName}.cs` in the same directory as the class
   - Add `: I{typeName}` to the class declaration
   - Add a `using` directive if needed
5. Return the generated interface code + summary of changes

### Output
```
Generated interface 'IMyService' with 5 members from 'MyService'

Created: Services/IMyService.cs
Modified: Services/MyService.cs (added : IMyService)

Interface:
  namespace MyApp.Services;

  public interface IMyService
  {
      string GetValue(int id);
      Task<bool> SaveAsync(Model model);
      ...
  }
```

---

## Tool 2: `implement_interface`

### Goal
Add stub implementations for unimplemented interface members on a class.

### Parameters
- `solutionPath` (string, required)
- `typeName` (string, required) — the class that needs implementations
- `interfaceName` (string, optional) — specific interface to implement. If omitted, implements all unimplemented members from all interfaces.

### Behavior
1. Resolve the class via SymbolResolver
2. Find all interfaces the class declares it implements
3. Identify which members are NOT yet implemented
4. Generate stub method/property bodies:
   - Methods: `throw new NotImplementedException();`
   - Properties: `get => throw new NotImplementedException(); set => throw new NotImplementedException();`
5. Insert the generated members into the class
6. Return summary of what was added

### Output
```
Implemented 3 members from 'IMyService' in 'MyService':
  + string GetValue(int id)
  + Task<bool> SaveAsync(Model model)
  + string Name { get; set; }
```

---

## Tool 3: `find_unused_code`

### Goal
Find types, methods, properties, and fields that have zero references across the solution.

### Parameters
- `solutionPath` (string, required)
- `scope` (string, optional, default: "all") — "types", "methods", "properties", "fields", or "all"
- `projectName` (string, optional) — restrict analysis to a specific project

### Behavior
1. Iterate all source-defined symbols of the requested kind(s)
2. For each symbol, use `SymbolFinder.FindReferencesAsync` to count references
3. Filter out:
   - Entry points (`Main` methods, top-level statements)
   - Symbols with special attributes (`[Fact]`, `[Test]`, `[McpServerTool]`, etc.)
   - Interface implementations (they may be called via interface)
   - Override methods (called via base class dispatch)
   - Public types in library projects (may be consumed externally)
   - Constructors called implicitly
4. Return list of unreferenced symbols with locations

### Output
```
Unused code in SharpMCP (7 symbols):
  Types:
    (none)
  Methods:
    private string FormatLegacy(...)  [Formatting/SymbolFormatter.cs:142]
  Properties:
    public string OldName { get; }  [Models/SymbolResult.cs:8]
  Fields:
    private readonly int _retryCount  [Services/WorkspaceManager.cs:12]
```

### Non-Functional
- Performance: This tool iterates every symbol and runs FindReferences. For large solutions, this will be slow. Consider adding a progress indicator or capping results.
- False positives: Some symbols (reflection, serialization, test fixtures) may appear unused but aren't. The exclusion list handles common cases.

---

## Tool 4: `change_signature`

### Goal
Modify a method's parameter list (add, remove, reorder) and update all call sites across the solution.

### Parameters
- `solutionPath` (string, required)
- `methodName` (string, required)
- `typeName` (string, optional) — containing type for disambiguation
- `addParameters` (string, optional) — comma-separated list of `type name=defaultValue` to add (e.g., `"string filter=null, int limit=10"`)
- `removeParameters` (string, optional) — comma-separated list of parameter names to remove
- `reorderParameters` (string, optional) — comma-separated ordered list of parameter names specifying new order

### Behavior
1. Resolve the method via SymbolResolver
2. Parse the requested changes
3. Compute the new parameter list
4. For added parameters with default values: update call sites to NOT pass the new param (default kicks in)
5. For added parameters without defaults: update call sites to pass `default(T)` or require user to specify
6. For removed parameters: remove the corresponding argument at each call site
7. For reordered parameters: reorder arguments at each call site (handling named arguments correctly)
8. Apply all changes atomically via Roslyn workspace
9. Return summary

### Output
```
Changed signature of 'MyService.GetData':
  Before: GetData(string query, int page, int pageSize)
  After:  GetData(string query, int pageSize, string filter = null)

  Removed: 'page'
  Reordered: 'pageSize' moved to position 2
  Added: 'string filter = null'

  Updated 12 call sites across 5 files
```

### Complexity Notes
- This is the most complex tool. Roslyn's public API does NOT include a direct "change signature" refactoring — it's internal to the IDE.
- Implementation approach: Use syntax rewriting (CSharpSyntaxRewriter) to modify the method declaration and all call sites found via SymbolFinder.FindCallersAsync.
- Handle edge cases: named arguments, params arrays, optional parameters, extension method calls, delegate invocations.
- Consider implementing a simpler version first (add-only or remove-only) and iterating.

---

## Implementation Order
1. **extract_interface** — Medium complexity, no existing Roslyn refactoring to leverage, but straightforward syntax generation
2. **implement_interface** — Low-medium, can reuse Roslyn's interface member detection
3. **find_unused_code** — Low complexity, builds on existing FindReferences infrastructure
4. **change_signature** — High complexity, implement last, consider phased approach

## Architecture Notes
- `extract_interface` and `implement_interface` → new `InterfaceService`
- `find_unused_code` → new `AnalysisService`
- `change_signature` → new `SignatureService`
- All tools go in `RefactoringTools` (write ops) or new `AnalysisTools` (read ops)
