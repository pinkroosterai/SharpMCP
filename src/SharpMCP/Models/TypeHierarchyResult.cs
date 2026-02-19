namespace SharpMCP.Models;

public record TypeHierarchyResult(
    string TypeName,
    string Kind,
    List<string> BaseTypes,
    List<string> Interfaces,
    List<SymbolResult>? Members = null
);
