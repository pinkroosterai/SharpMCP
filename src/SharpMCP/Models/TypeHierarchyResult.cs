namespace SharpMCP.Models;

public record TypeHierarchyResult(
    string TypeName,
    string Kind,
    IReadOnlyList<string> BaseTypes,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<SymbolResult>? Members = null
);
