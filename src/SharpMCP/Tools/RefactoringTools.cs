using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class RefactoringTools
{
    private readonly RenameService _renameService;

    public RefactoringTools(RenameService renameService)
    {
        _renameService = renameService;
    }

    [McpServerTool(Name = "rename_symbol"), Description("Rename a symbol (class, interface, method, property, etc.) and update all references across the solution. When renaming a type whose filename matches, the file is also renamed.")]
    public async Task<string> RenameSymbol(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Current symbol name to rename")] string symbolName,
        [Description("The new name for the symbol")] string newName,
        [Description("Optional containing type name (to disambiguate members)")] string? typeName = null,
        [Description("Also rename occurrences in string literals and comments (default: false)")] bool includeStrings = false)
    {
        try
        {
            return await _renameService.RenameSymbolAsync(
                solutionPath, symbolName, newName, typeName, includeStrings);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
