using Microsoft.CodeAnalysis;

namespace SharpMCP.Formatting;

public sealed class LocationFormatter
{
    public string FormatLocation(Location location, string? solutionDir = null)
    {
        if (location.SourceTree == null)
            return "(no source)";

        var (displayPath, line) = ResolveDisplayPathAndLine(location, solutionDir);
        return $"{displayPath}:{line}";
    }

    public string FormatWithSnippet(Location location, string? solutionDir = null, int contextLines = 0)
    {
        if (location.SourceTree == null)
            return "(no source)";

        var (displayPath, line) = ResolveDisplayPathAndLine(location, solutionDir);

        var sourceText = location.SourceTree.GetText();
        var targetLine = location.GetLineSpan().StartLinePosition.Line;

        if (targetLine >= 0 && targetLine < sourceText.Lines.Count)
        {
            var snippet = sourceText.Lines[targetLine].ToString().Trim();
            return $"{displayPath}:{line} - {snippet}";
        }

        return $"{displayPath}:{line}";
    }

    private static (string DisplayPath, int Line) ResolveDisplayPathAndLine(Location location, string? solutionDir)
    {
        var filePath = location.SourceTree!.FilePath;
        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        var displayPath = solutionDir != null ? MakePathRelative(filePath, solutionDir) : filePath;
        return (displayPath, line);
    }

    public static string MakePathRelative(string fullPath, string basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return fullPath;

        var baseUri = new Uri(basePath.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? basePath
            : basePath + Path.DirectorySeparatorChar);
        var fullUri = new Uri(fullPath);

        if (baseUri.IsBaseOf(fullUri))
        {
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        return fullPath;
    }
}
