using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Formatting;
using SharpMCP.Models;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class SymbolTools
{
    private readonly SymbolSearchService _symbolService;
    private readonly SymbolFormatter _formatter;

    public SymbolTools(SymbolSearchService symbolService, SymbolFormatter formatter)
    {
        _symbolService = symbolService;
        _formatter = formatter;
    }

    [McpServerTool(Name = "find_symbol"), Description("Search for symbols by name. Use exact=true for precise lookup with detailed info, or substring matching (default) for broader search.")]
    public async Task<string> FindSymbol(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Symbol name or substring to search for")] string query,
        [Description("Optional kind filter: class, interface, method, property, field, enum, struct, event")] string? kind = null,
        [Description("true = exact name match, false = substring match (default)")] bool exact = false,
        [Description("'compact' (default) or 'full' for detailed output including source and docs")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _symbolService.FindSymbolsAsync(solutionPath, query, kind, exact, detailLevel);
            if (results.Count == 0)
                return $"No symbols found matching '{query}'.";

            return $"Symbols matching '{query}' ({results.Count}):\n" +
                _formatter.FormatSymbolList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_file_symbols"), Description("List all symbols (classes, methods, properties) in a specific source file. Use depth=1 to include members of types.")]
    public async Task<string> GetFileSymbols(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Path to the source file (absolute or relative to solution)")] string filePath,
        [Description("Depth: 0 for types only, 1 to include members")] int depth = 0,
        [Description("'compact' (default) or 'full' for detailed output")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _symbolService.GetFileSymbolsAsync(solutionPath, filePath, depth);
            if (results.Count == 0)
                return $"No symbols found in '{filePath}'.";

            return $"Symbols in {Path.GetFileName(filePath)} ({results.Count}):\n" +
                _formatter.FormatSymbolList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_type_members"), Description("List all members (methods, properties, fields, events) of a specific type.")]
    public async Task<string> GetTypeMembers(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Type name (simple or fully qualified)")] string typeName,
        [Description("'compact' (default) or 'full' for detailed output including source")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _symbolService.GetTypeMembersAsync(solutionPath, typeName, detailLevel);
            if (results.Count == 0)
                return $"No members found in type '{typeName}'.";

            return $"Members of {typeName} ({results.Count}):\n" +
                _formatter.FormatSymbolList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_namespaces"), Description("List all namespaces in a solution.")]
    public async Task<string> ListNamespaces(
        [Description("Path to .sln or .csproj file")] string solutionPath)
    {
        try
        {
            var namespaces = await _symbolService.ListNamespacesAsync(solutionPath);
            if (namespaces.Count == 0)
                return "No namespaces found.";

            return $"Namespaces ({namespaces.Count}):\n" +
                string.Join("\n", namespaces.Select(n => $"  {n}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
