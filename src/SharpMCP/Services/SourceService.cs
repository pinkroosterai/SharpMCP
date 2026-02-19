using SharpMCP.Formatting;

namespace SharpMCP.Services;

public sealed class SourceService
{
    private readonly SymbolResolver _symbolResolver;

    public SourceService(SymbolResolver symbolResolver)
    {
        _symbolResolver = symbolResolver;
    }

    public async Task<string> GetSymbolSourceAsync(string solutionPath, string symbolName, string? typeName = null)
    {
        var symbol = await _symbolResolver.ResolveSymbolAsync(solutionPath, symbolName, typeName);

        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Symbol '{symbolName}' has no source declaration (may be from metadata).");

        var node = await syntaxRef.GetSyntaxAsync();
        var location = node.GetLocation();
        var lineSpan = location.GetLineSpan();
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var filePath = location.SourceTree?.FilePath ?? "(unknown)";
        var displayPath = LocationFormatter.MakePathRelative(filePath, solutionDir);
        var startLine = lineSpan.StartLinePosition.Line + 1;

        return $"// {displayPath}:{startLine}\n{node.ToFullString().TrimEnd()}";
    }

    private static readonly long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    public static async Task<string> GetFileContentAsync(string filePath, int? startLine = null, int? endLine = null)
    {
        var absolutePath = Path.GetFullPath(filePath);

        if (!File.Exists(absolutePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var fileInfo = new FileInfo(absolutePath);
        if (fileInfo.Length > MaxFileSizeBytes)
            throw new InvalidOperationException(
                $"File is too large ({fileInfo.Length / 1024}KB). Use startLine/endLine to read a portion.");

        var allLines = await File.ReadAllLinesAsync(absolutePath);
        var start = Math.Max(0, (startLine ?? 1) - 1);
        var end = Math.Min(allLines.Length, endLine ?? allLines.Length);

        var numberedLines = new List<string>();
        for (int i = start; i < end; i++)
        {
            numberedLines.Add($"{i + 1,5} | {allLines[i]}");
        }

        return string.Join("\n", numberedLines);
    }
}
