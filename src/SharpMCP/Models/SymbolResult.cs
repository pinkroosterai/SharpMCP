namespace SharpMCP.Models;

public record SymbolResult(
    string Name,
    string FullyQualifiedName,
    string Kind,
    string Signature,
    string FilePath,
    int Line,
    string? DocComment = null,
    string? SourceBody = null
);
