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

    [McpServerTool(Name = "find_references"), Description("Find all references to a symbol across the solution. Use mode='callers' for method call sites only, or mode='usages' for type usage sites.")]
    public async Task<string> FindReferences(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Symbol name to find references for")] string symbolName,
        [Description("Optional containing type name (to disambiguate)")] string? typeName = null,
        [Description("'all' (default), 'callers' (method call sites only), or 'usages' (type usage sites)")] string mode = "all",
        [Description("Optional project name to restrict search to")] string? projectName = null,
        [Description("'compact' (default) = reference locations. 'full' = includes surrounding context lines.")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _referencesService.FindReferencesAsync(
                solutionPath, symbolName, typeName, projectName, detailLevel, mode);

            if (results.Count == 0)
                return $"No references found for '{symbolName}'.";

            var header = mode.ToLowerInvariant() switch
            {
                "callers" => $"Callers of {symbolName}",
                "usages" => $"Usages of {symbolName}",
                _ => $"References to {symbolName}",
            };

            return $"{header} ({results.Count}):\n" +
                _formatter.FormatReferenceList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
