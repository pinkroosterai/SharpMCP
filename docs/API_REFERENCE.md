# SharpMCP API Reference

Complete reference for all 26 MCP tools. All tools communicate via stdio MCP transport and return plain text (not JSON).

## Common Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `solutionPath` | string | required | Path to `.sln` or `.csproj` file. Cached after first load. |
| `detail` | string | `"compact"` | `"compact"` = signatures + locations. `"full"` = adds source bodies, doc comments, context lines. |
| `typeName` | string | optional | Containing type name for disambiguation when multiple symbols share a name. |

## Error Format

All tools catch exceptions and return: `"Error: <message>"`

---

## Project Structure Tools

### `list_projects`

List all projects in a solution with metadata.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |

**Output format:**
```
Solution: MySolution (3 projects)
  MyApp                          net9.0     Exe        42 files -> MyLib
  MyLib                          net9.0     Library    18 files
  MyTests                        net9.0     Exe        12 files -> MyApp, MyLib
```

---

### `get_project_info`

Get detailed metadata for a specific project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `projectName` | string | yes | — | Project name within solution |

---

### `list_project_references`

List project-to-project references (dependency graph).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `projectName` | string | yes | — | Project name |

**Output:** One referenced project name per line.

---

### `list_package_references`

List NuGet packages for a project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `projectName` | string | yes | — | Project name |

**Output:** `PackageName Version` per line. Parsed from `.csproj` XML.

---

### `list_source_files`

List all `.cs` files in a project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `projectName` | string | yes | — | Project name |

---

### `get_diagnostics`

Get compilation errors and warnings.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `projectName` | string | no | null | Omit for all projects |

**Output:** `Severity Id: Message  [path:line]` per diagnostic. Errors first, then by file/line.

---

## Symbol Search Tools

### `find_symbol`

Search for symbols by name (case-insensitive substring match).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `query` | string | yes | — | Symbol name or substring |
| `kind` | string | no | null | Filter: class, interface, method, property, field, enum, struct, event |
| `detail` | string | no | "compact" | "compact" or "full" |

**Output (compact):**
```
  public class WorkspaceManager  [Services/WorkspaceManager.cs:8]
  public class SymbolResolver  [Services/SymbolResolver.cs:6]
```

**Output (full):** Adds doc comment + indented source body per symbol.

---

### `get_file_symbols`

List all symbols in a specific source file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `filePath` | string | yes | — | Path to source file (absolute or relative to solution) |
| `depth` | int | no | 0 | 0 = types only, 1 = include members |
| `detail` | string | no | "compact" | "compact" or "full" |

---

### `get_type_members`

List all members of a specific type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Type name (simple or fully qualified) |
| `detail` | string | no | "compact" | "compact" or "full" (full includes source bodies) |

---

### `get_symbol_info`

Get detailed information about a specific symbol.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `symbolName` | string | yes | — | Symbol name |
| `detail` | string | no | "compact" | "compact" or "full" |

---

### `list_namespaces`

List all namespaces in a solution (source-defined only, excludes metadata).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |

---

## Type Hierarchy Tools

### `find_implementations`

Find all classes implementing an interface.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `interfaceName` | string | yes | — | Interface name (e.g., "IOrderService") |
| `detail` | string | no | "compact" | "compact" or "full" |

Uses `SymbolFinder.FindImplementationsAsync`. Results filtered to source-defined types only.

---

### `find_subclasses`

Find all classes inheriting from a base class.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `baseClassName` | string | yes | — | Base class name |
| `detail` | string | no | "compact" | "compact" or "full" |

---

### `get_type_hierarchy`

Get the full inheritance chain for a type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Type name |

**Output:**
```
OrderService
  bases: BaseService -> object
  implements: IOrderService, IDisposable
```

---

### `find_overrides`

Find all overrides of a virtual/abstract method.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Type containing the method |
| `methodName` | string | yes | — | Method name |
| `detail` | string | no | "compact" | "compact" or "full" |

Validates the method is virtual, abstract, or override before searching.

---

## Reference Tools

### `find_references`

Find all locations where a symbol is referenced.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `symbolName` | string | yes | — | Symbol name |
| `typeName` | string | no | null | Containing type (disambiguation) |
| `projectScope` | string | no | null | Restrict to a project |
| `detail` | string | no | "compact" | "compact" or "full" (full adds context lines) |

**Output (compact):**
```
  Services/WorkspaceManager.cs:45 [in ProjectService.ListProjectsAsync] - var solution = await _workspaceManager.GetSolutionAsync(path);
```

**Output (full):** Adds 2 lines of context before and after each reference.

---

### `find_callers`

Find all call sites for a method.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `methodName` | string | yes | — | Method name |
| `typeName` | string | no | null | Containing type (disambiguation) |
| `detail` | string | no | "compact" | "compact" or "full" |

---

### `find_usages`

Find where a type is used (parameters, return types, fields, variables).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Type name |
| `detail` | string | no | "compact" | "compact" or "full" |

---

## Source Retrieval Tools

### `get_source`

Get the source code of a specific symbol (method body, class declaration, etc.).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `symbolName` | string | yes | — | Symbol name |
| `typeName` | string | no | null | Containing type (disambiguation) |

Returns the full syntax node text of the symbol declaration.

---

### `get_file_content`

Get file content with line numbers. Does not require a solution path.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | yes | — | Path to source file |
| `startLine` | int | no | null | Start line (1-based) |
| `endLine` | int | no | null | End line (1-based, inclusive) |

**Output:** `  42 | code here` format. 5 MB file size limit.

---

## Refactoring Tools

### `rename_symbol`

Rename a symbol and update all references across the solution. Writes changes to disk.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `symbolName` | string | yes | — | Current symbol name |
| `newName` | string | yes | — | New name |
| `typeName` | string | no | null | Containing type (disambiguation) |
| `includeStrings` | bool | no | false | Also rename in string literals and comments |

**Restrictions:** Only NamedType, Method, Property, Field, Event. Validates name format. When renaming a type whose filename matches, the file is also renamed via `File.Move`.

---

### `extract_interface`

Generate an interface from a class's public members.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Class to extract from |
| `interfaceName` | string | no | null | Interface name (defaults to `I{TypeName}`) |
| `apply` | bool | no | true | true = create file + modify class. false = preview only. |

Collects public non-static methods, properties, and events (excludes constructors, operators, finalizers). Handles generics, ref/out/in/params parameters, init-only setters.

---

### `implement_interface`

Add stub implementations for unimplemented interface members.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `typeName` | string | yes | — | Class needing implementations |
| `interfaceName` | string | no | null | Specific interface (omit for all unimplemented) |

Stubs use `throw new NotImplementedException()` as expression body.

---

### `change_signature`

Modify a method's parameter list and update all call sites.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `methodName` | string | yes | — | Method name |
| `typeName` | string | no | null | Containing type (disambiguation) |
| `addParameters` | string | no | null | Comma-separated: `"type name=default"` (e.g., `"string filter=null, int limit=10"`) |
| `removeParameters` | string | no | null | Comma-separated parameter names to remove |
| `reorderParameters` | string | no | null | Comma-separated names in new order |

**Add behavior:** Params with defaults — call sites unchanged. Params without defaults — `default(T)` inserted at call sites.
**Remove behavior:** Corresponding arguments removed at all call sites.
**Reorder behavior:** Arguments reordered. Handles named arguments. Unlisted params appended after reordered ones.

---

## Analysis Tools

### `find_unused_code`

Find symbols with zero references across the solution.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `scope` | string | no | "all" | "all", "types", "methods", "properties", "fields" |
| `projectName` | string | no | null | Restrict to specific project |

**Smart exclusions:** Entry points (Main, Program), test attributes ([Fact], [Test], [Theory]), MCP attributes ([McpServerTool]), interface implementations, overrides, public types, public const fields, [Obsolete] symbols.

**Output:**
```
Unused code in SharpMCP (3 symbols):
  Types:
    (none)
  Methods:
    private string FormatLegacy(...)  [Formatting/SymbolFormatter.cs:142]
  Properties:
    (none)
  Fields:
    private readonly int _retryCount  [Services/WorkspaceManager.cs:12]
```

---

### `find_code_smells`

Detect code smells across a solution. 13 detectors grouped into complexity, design, and inheritance categories. Results sorted by severity.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `solutionPath` | string | yes | — | Path to .sln or .csproj |
| `category` | string | no | "all" | "all", "complexity", "design", or "inheritance" |
| `projectName` | string | no | null | Restrict to specific project |
| `deep` | bool | no | false | Enable expensive checks (feature envy detection) |

**Smell categories and thresholds:**

| Category | Smell | Warning | Critical |
|----------|-------|---------|----------|
| Complexity | Long method | >50 lines | >100 lines |
| Complexity | Deep nesting | >3 levels | >5 levels |
| Complexity | High cyclomatic complexity | >10 | >20 |
| Complexity | Large class | >20 members | >40 members |
| Complexity | Long parameter list | >5 params | >8 params |
| Design | God class | — | >20 members AND >5 distinct dependency types |
| Design | Data class | info | — |
| Design | Too many dependencies | >5 ctor params | >8 ctor params |
| Design | Middle man | >80% single-delegation methods | — |
| Design | Feature envy (deep only) | external accesses > own type | — |
| Inheritance | Deep hierarchy | >3 levels | — |
| Inheritance | Refused bequest | <20% override rate (≥3 virtual members) | — |
| Inheritance | Speculative generality | unused type parameter | — |

**Smart exclusions:** Enums, delegates, interfaces, records (for data class), static classes (for god class / too many deps), `Program` / `<Program>$`, excluded attributes (test, MCP, serialization, ASP.NET).

**Output:**
```
Code smells in SharpMCP (category: all, 5 issues):

=== CRITICAL ===

Long method (>100 lines):
  ChangeSignatureAsync (142 lines)  [Services/SignatureService.cs:45]

=== WARNING ===

High cyclomatic complexity (>10):
  ChangeSignatureAsync (CC=14)  [Services/SignatureService.cs:45]

Long parameter list (>5 params):
  MyService..ctor (7 params)  [Services/MyService.cs:12]

=== INFO ===

Data class (0 methods, ≥2 properties):
  ProjectInfo (5 properties)  [Models/ProjectInfo.cs:3]
```
