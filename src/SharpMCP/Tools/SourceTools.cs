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

    [McpServerTool(Name = "get_source"), Description("Get the full source code of a named symbol (method, class, property, etc.). To read arbitrary file ranges, use get_file_content instead.")]
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

    [McpServerTool(Name = "get_file_content"), Description("Get file content with line numbers. Works with any text file (not just .cs). Supports optional line range. 5 MB file size limit.")]
    public async Task<string> GetFileContent(
        [Description("Path to the source file")] string filePath,
        [Description("Optional start line (1-based)")] int? startLine = null,
        [Description("Optional end line (1-based, inclusive)")] int? endLine = null)
    {
        try
        {
            if (startLine.HasValue && endLine.HasValue && startLine.Value > endLine.Value)
                return $"Error: startLine ({startLine}) cannot be greater than endLine ({endLine}).";

            return await SourceService.GetFileContentAsync(filePath, startLine, endLine);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
