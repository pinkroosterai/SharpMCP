using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Formatting;
using SharpMCP.Models;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class ReferenceTools
{
    private readonly ReferencesService _referencesService;
    private readonly SymbolFormatter _formatter;

    public ReferenceTools(ReferencesService referencesService, SymbolFormatter formatter)
    {
        _referencesService = referencesService;
        _formatter = formatter;
    }

    [McpServerTool(Name = "find_references"), Description("Find all locations where a symbol is referenced. Supports scoping to a specific project.")]
    public async Task<string> FindReferences(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Symbol name to find references for")] string symbolName,
        [Description("Optional containing type name (to disambiguate)")] string? typeName = null,
        [Description("Optional project name to scope the search")] string? projectScope = null,
        [Description("'compact' (default) or 'full' for detailed output with context")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _referencesService.FindReferencesAsync(
                solutionPath, symbolName, typeName, projectScope, detailLevel);

            if (results.Count == 0)
                return $"No references found for '{symbolName}'.";

            return $"References to {symbolName} ({results.Count}):\n" +
                _formatter.FormatReferenceList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "find_callers"), Description("Find all call sites for a specific method. Returns caller location and code snippet.")]
    public async Task<string> FindCallers(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Method name to find callers of")] string methodName,
        [Description("Optional containing type name (to disambiguate overloads)")] string? typeName = null,
        [Description("'compact' (default) or 'full' for detailed output with context")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _referencesService.FindCallersAsync(
                solutionPath, methodName, typeName, detailLevel);

            if (results.Count == 0)
                return $"No callers found for '{methodName}'.";

            return $"Callers of {methodName} ({results.Count}):\n" +
                _formatter.FormatReferenceList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "find_usages"), Description("Find all locations where a type is used (as parameter, return type, field, variable, etc.).")]
    public async Task<string> FindUsages(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Type name to find usages of")] string typeName,
        [Description("'compact' (default) or 'full' for detailed output with context")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _referencesService.FindUsagesAsync(solutionPath, typeName, detailLevel);

            if (results.Count == 0)
                return $"No usages found for '{typeName}'.";

            return $"Usages of {typeName} ({results.Count}):\n" +
                _formatter.FormatReferenceList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
