namespace SharpMCP.Models;

public record ReferenceResult(
    string FilePath,
    int Line,
    int Column,
    string CodeSnippet,
    string? ContextBefore = null,
    string? ContextAfter = null,
    string? ContainingSymbol = null
);
