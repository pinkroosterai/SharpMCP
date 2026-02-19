using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class AnalysisTools
{
    private readonly AnalysisService _analysisService;

    public AnalysisTools(AnalysisService analysisService)
    {
        _analysisService = analysisService;
    }

    [McpServerTool(Name = "find_unused_code"), Description("Find types, methods, properties, and fields with zero references across the solution. Filters out entry points, test methods, interface implementations, overrides, and public API types.")]
    public async Task<string> FindUnusedCode(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("What to scan: 'all', 'types', 'methods', 'properties', or 'fields' (default: 'all')")] string scope = "all",
        [Description("Optional project name to restrict analysis to")] string? projectName = null)
    {
        try
        {
            return await _analysisService.FindUnusedCodeAsync(solutionPath, scope, projectName);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "find_code_smells"), Description("Detect code smells across a solution. Complexity: long methods, deep nesting, high cyclomatic complexity, large classes, long parameter lists. Design: god classes, data classes, too many dependencies, middle man, feature envy (deep only). Inheritance: deep hierarchy, refused bequest, speculative generality. Results grouped by severity.")]
    public async Task<string> FindCodeSmells(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Filter: 'all', 'complexity', 'design', or 'inheritance' (default: 'all')")] string category = "all",
        [Description("Optional project name to restrict analysis to")] string? projectName = null,
        [Description("Enable expensive checks like feature envy detection (default: false)")] bool deep = false)
    {
        try
        {
            return await _analysisService.FindCodeSmellsAsync(solutionPath, category, projectName, deep);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
