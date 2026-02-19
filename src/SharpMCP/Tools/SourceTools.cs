using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class SourceTools
{
    private readonly SourceService _sourceService;

    public SourceTools(SourceService sourceService)
    {
        _sourceService = sourceService;
    }

    [McpServerTool(Name = "get_source"), Description("Get the source code of a specific symbol (method body, class declaration, etc.).")]
    public async Task<string> GetSource(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Symbol name to retrieve source for")] string symbolName,
        [Description("Optional containing type name (to disambiguate members)")] string? typeName = null)
    {
        try
        {
            return await _sourceService.GetSymbolSourceAsync(solutionPath, symbolName, typeName);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_file_content"), Description("Get the content of a source file with line numbers. Supports optional line range.")]
    public async Task<string> GetFileContent(
        [Description("Path to the source file")] string filePath,
        [Description("Optional start line (1-based)")] int? startLine = null,
        [Description("Optional end line (1-based, inclusive)")] int? endLine = null)
    {
        try
        {
            return await SourceService.GetFileContentAsync(filePath, startLine, endLine);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
