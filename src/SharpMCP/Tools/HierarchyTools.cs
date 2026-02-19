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

    [McpServerTool(Name = "find_implementations"), Description("Find all classes that implement a given interface. Works across all projects in the solution.")]
    public async Task<string> FindImplementations(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Interface name (e.g., 'IOrderService')")] string interfaceName,
        [Description("'compact' (default) or 'full' for detailed output")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _hierarchyService.FindImplementationsAsync(solutionPath, interfaceName, detailLevel);
            if (results.Count == 0)
                return $"No implementations found for '{interfaceName}'.";

            return $"Classes implementing {interfaceName} ({results.Count}):\n" +
                _formatter.FormatSymbolList(results, detailLevel);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "find_subclasses"), Description("Find all classes that inherit from a given base class.")]
    public async Task<string> FindSubclasses(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Base class name (e.g., 'BaseController')")] string baseClassName,
        [Description("'compact' (default) or 'full' for detailed output")] string detail = "compact")
    {
        try
        {
            var detailLevel = DetailLevelExtensions.Parse(detail);
            var results = await _hierarchyService.FindSubclassesAsync(solutionPath, baseClassName, detailLevel);
            if (results.Count == 0)
                return $"No subclasses found for '{baseClassName}'.";

            return $"Classes extending {baseClassName} ({results.Count}):\n" +
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
