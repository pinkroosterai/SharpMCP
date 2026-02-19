using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class RefactoringTools
{
    private readonly RenameService _renameService;
    private readonly InterfaceService _interfaceService;
    private readonly SignatureService _signatureService;

    public RefactoringTools(RenameService renameService, InterfaceService interfaceService, SignatureService signatureService)
    {
        _renameService = renameService;
        _interfaceService = interfaceService;
        _signatureService = signatureService;
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

    [McpServerTool(Name = "extract_interface"), Description("Generate an interface from a class's public members. Optionally creates the interface file and adds it to the class declaration.")]
    public async Task<string> ExtractInterface(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("The class to extract an interface from")] string typeName,
        [Description("Name for the interface (defaults to I{TypeName})")] string? interfaceName = null,
        [Description("When true, creates the file and modifies the class. When false, returns preview only.")] bool apply = true)
    {
        try
        {
            return await _interfaceService.ExtractInterfaceAsync(
                solutionPath, typeName, interfaceName, apply);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "implement_interface"), Description("Add stub implementations (throw NotImplementedException) for unimplemented interface members on a class.")]
    public async Task<string> ImplementInterface(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("The class that needs interface implementations")] string typeName,
        [Description("Specific interface to implement. If omitted, implements all unimplemented members.")] string? interfaceName = null)
    {
        try
        {
            return await _interfaceService.ImplementInterfaceAsync(
                solutionPath, typeName, interfaceName);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "change_signature"), Description("Modify a method's parameter list (add, remove, reorder) and update all call sites across the solution.")]
    public async Task<string> ChangeSignature(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Method name to modify")] string methodName,
        [Description("Optional containing type name (to disambiguate)")] string? typeName = null,
        [Description("Comma-separated parameters to add: 'type name=defaultValue' (e.g., 'string filter=null, int limit=10')")] string? addParameters = null,
        [Description("Comma-separated parameter names to remove")] string? removeParameters = null,
        [Description("Comma-separated parameter names in desired new order")] string? reorderParameters = null)
    {
        try
        {
            return await _signatureService.ChangeSignatureAsync(
                solutionPath, methodName, typeName,
                addParameters, removeParameters, reorderParameters);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
