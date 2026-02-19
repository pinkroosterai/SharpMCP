# Requirements: rename_symbol Tool

## Goal
Add a `rename_symbol` MCP tool to SharpMCP that renames a C# symbol and automatically updates all references across the solution. This is SharpMCP's first write/mutation operation.

## Functional Requirements

### FR-1: Single `rename_symbol` Tool
- One MCP tool named `rename_symbol`
- Parameters:
  - `solutionPath` (string, required) — path to .sln or .csproj
  - `symbolName` (string, required) — current symbol name to rename
  - `newName` (string, required) — the new name for the symbol
  - `typeName` (string, optional) — containing type for disambiguation (e.g., rename method `Get` in type `MyService`)
  - `includeStrings` (bool, optional, default: false) — also rename occurrences in string literals and comments
- Returns: summary string with changed files + reference count

### FR-2: Renamable Symbol Types
- **Types**: class, interface, struct, enum, record
- **Members**: method, property, field, event
- Local variables and namespaces are explicitly out of scope

### FR-3: Cross-Solution Reference Updates
- All references across all projects in the solution must be updated
- Includes: usages, type annotations, constructor calls, base class references, interface implementations, overrides, XML doc `<see cref="..."/>` tags
- Uses Roslyn's `Renamer.RenameSymbolAsync` for correctness

### FR-4: File Rename for Types
- When renaming a type (class, interface, struct, enum, record), if the containing file name matches the old type name (e.g., `MyClass.cs`), rename the file to match the new name (e.g., `NewClass.cs`)
- Only applies to types, not members
- Only renames if filename matches old name exactly (case-insensitive)

### FR-5: Comment/String Rename (Optional)
- When `includeStrings` is true, also rename occurrences in:
  - String literals
  - XML documentation comments
  - Regular comments
- Default: false (code-only)

### FR-6: Output Format
- Return a summary string like:
  ```
  Renamed 'MyClass' → 'NewClass' across 7 files (12 references updated)
  Changed files:
    Services/MyService.cs
    Models/MyClass.cs → Models/NewClass.cs (file renamed)
    Tools/SymbolTools.cs
    ...
  ```

## Non-Functional Requirements

### NFR-1: Workspace Cache Invalidation
- After applying a rename, the WorkspaceManager cache must be invalidated for the affected solution
- Subsequent tool calls must see the renamed symbols

### NFR-2: Conflict Detection
- If the new name conflicts with an existing symbol in scope, return an error message describing the conflict
- Do NOT apply partial renames

### NFR-3: Validation
- Validate `newName` is a legal C# identifier before attempting rename
- Validate the symbol exists (reuse existing SymbolResolver)
- Return clear error messages for: symbol not found, ambiguous symbol, invalid identifier, conflicts

### NFR-4: Atomicity
- Either all changes apply successfully, or none do
- Roslyn's `Solution` immutability ensures this — we only write to disk after computing the full rename

## Architecture Notes

### Roslyn API
```csharp
// Core rename API
var newSolution = await Renamer.RenameSymbolAsync(
    solution, symbol, new SymbolRenameOptions(RenameInStrings: includeStrings, RenameInComments: includeStrings),
    newName);

// Apply changes to disk
workspace.TryApplyChanges(newSolution);
```

### New Components
- **Service**: `RenameService` — orchestrates symbol resolution, rename, file rename, cache invalidation
- **Tool**: Add `rename_symbol` method to existing `SymbolTools` class (or new `RefactoringTools` class)

### Integration Points
- `SymbolResolver` — reuse for finding the target symbol
- `WorkspaceManager` — need access to underlying `MSBuildWorkspace` for `TryApplyChanges`, plus cache invalidation
- `LocationFormatter` — reuse for formatting changed file paths

## Open Questions
- None — all requirements clarified through brainstorm session

## Next Steps
- `/sc:design` for detailed architecture
- `/sc:implement` for implementation
