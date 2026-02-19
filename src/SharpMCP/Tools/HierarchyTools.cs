using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Formatting;
using SharpMCP.Models;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class HierarchyTools
{
    private readonly HierarchyService _hierarchyService;
    private readonly SymbolFormatter _formatter;

    public HierarchyTools(HierarchyService hierarchyService, SymbolFormatter formatter)
    {
        _hierarchyService = hierarchyService;
        _formatter = formatter;
    }

    [McpServerTool(Name = "find_derived_types"), Description("Find classes implementing an interface or inheriting from a base class. Automatically detects whether the type is an interface or class.")]
    public async Task<string> FindDerivedTypes(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Interface or base class name")] string typeName,
        [Description("'compact' (default) or 'full' for detailed output")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var (results, typeKind) = await _hierarchyService.FindDerivedTypesAsync(solutionPath, typeName, detailLevel);

            if (results.Count == 0)
                return $"No derived types found for '{typeName}'.";

            var header = typeKind == "interface"
                ? $"Classes implementing {typeName}"
                : $"Classes extending {typeName}";

            return $"{header} ({results.Count}):\n" +
                _formatter.FormatSymbolList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_type_hierarchy"), Description("Get the full inheritance chain for a type (base classes + all interfaces).")]
    public async Task<string> GetTypeHierarchy(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Type name (e.g., 'OrderService')")] string typeName)
    {
        try
        {
            var result = await _hierarchyService.GetTypeHierarchyAsync(solutionPath, typeName);
            return _formatter.FormatTypeHierarchy(result);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "find_overrides"), Description("Find all overrides of a virtual/abstract method across the solution.")]
    public async Task<string> FindOverrides(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Type name containing the method")] string typeName,
        [Description("Method name to find overrides for")] string methodName,
        [Description("'compact' (default) or 'full' for detailed output")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _hierarchyService.FindOverridesAsync(solutionPath, typeName, methodName, detailLevel);
            if (results.Count == 0)
                return $"No overrides found for '{typeName}.{methodName}'.";

            return $"Overrides of {typeName}.{methodName} ({results.Count}):\n" +
                _formatter.FormatSymbolList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
