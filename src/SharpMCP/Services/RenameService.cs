using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using SharpMCP.Formatting;

namespace SharpMCP.Services;

public sealed class RenameService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SymbolResolver _symbolResolver;

    public RenameService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
    {
        _workspaceManager = workspaceManager;
        _symbolResolver = symbolResolver;
    }

    public async Task<string> RenameSymbolAsync(
        string solutionPath, string symbolName, string newName,
        string? typeName = null, bool includeStrings = false)
    {
        // Validate new name is a legal C# identifier
        ValidateIdentifier(newName);

        // Resolve the symbol
        var symbol = await _symbolResolver.ResolveSymbolAsync(solutionPath, symbolName, typeName);

        // Check if symbol is a renamable type (types + members only)
        ValidateRenamableSymbol(symbol);

        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var oldName = symbol.Name;

        // Determine if we should rename the file (only for type symbols)
        var isTypeSymbol = symbol is INamedTypeSymbol;
        string? oldFilePath = null;
        string? newFilePath = null;

        if (isTypeSymbol)
        {
            var sourceLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (sourceLocation?.SourceTree?.FilePath is string filePath)
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (string.Equals(fileName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    oldFilePath = filePath;
                    newFilePath = Path.Combine(
                        Path.GetDirectoryName(filePath)!,
                        newName + Path.GetExtension(filePath));
                }
            }
        }

        // Perform the rename via Roslyn
        var options = new SymbolRenameOptions(
            RenameOverloads: false,
            RenameInStrings: includeStrings,
            RenameInComments: includeStrings,
            RenameFile: false); // We handle file rename ourselves

        var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName);

        // Compute changed documents
        var changedDocs = await GetChangedDocumentsAsync(solution, newSolution);

        // Apply changes, rename file if needed, and invalidate cache — all under one lock
        Action? postApply = (oldFilePath != null && newFilePath != null)
            ? () => { if (File.Exists(oldFilePath)) File.Move(oldFilePath, newFilePath); }
            : null;
        await _workspaceManager.ApplyChangesAndInvalidateAsync(solutionPath, newSolution, postApply);

        // Build summary
        return BuildSummary(oldName, newName, changedDocs, solutionDir, oldFilePath, newFilePath);
    }

    private static void ValidateIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("New name cannot be empty.");

        // C# identifier: starts with letter or underscore, followed by letters, digits, or underscores
        // Also allow @ prefix for verbatim identifiers
        if (!Regex.IsMatch(name, @"^@?[\p{L}_][\p{L}\p{Nd}_]*$"))
            throw new ArgumentException(
                $"'{name}' is not a valid C# identifier.");
    }

    private static void ValidateRenamableSymbol(ISymbol symbol)
    {
        var allowed = symbol.Kind is
            SymbolKind.NamedType or
            SymbolKind.Method or
            SymbolKind.Property or
            SymbolKind.Field or
            SymbolKind.Event;

        if (!allowed)
            throw new ArgumentException(
                $"Cannot rename {symbol.Kind} symbols. Supported: class, interface, struct, enum, record, method, property, field, event.");

        if (!symbol.Locations.Any(l => l.IsInSource))
            throw new ArgumentException(
                $"Cannot rename '{symbol.Name}' — it is defined in metadata, not in source.");
    }

    private static async Task<List<(string FilePath, DocumentId Id)>> GetChangedDocumentsAsync(Solution oldSolution, Solution newSolution)
    {
        var changed = new List<(string FilePath, DocumentId Id)>();

        foreach (var projectId in newSolution.ProjectIds)
        {
            var newProject = newSolution.GetProject(projectId);
            var oldProject = oldSolution.GetProject(projectId);
            if (newProject == null || oldProject == null) continue;

            foreach (var docId in newProject.DocumentIds)
            {
                var newDoc = newProject.GetDocument(docId);
                var oldDoc = oldProject.GetDocument(docId);

                if (newDoc == null) continue;

                // New document or changed content
                if (oldDoc == null)
                {
                    changed.Add((newDoc.FilePath ?? "(unknown)", docId));
                }
                else
                {
                    var newText = await newDoc.GetTextAsync();
                    var oldText = await oldDoc.GetTextAsync();
                    if (!newText.ContentEquals(oldText))
                    {
                        changed.Add((newDoc.FilePath ?? "(unknown)", docId));
                    }
                }
            }
        }

        return changed;
    }

    private static string BuildSummary(
        string oldName, string newName,
        List<(string FilePath, DocumentId Id)> changedDocs,
        string solutionDir, string? oldFilePath, string? newFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Renamed '{oldName}' → '{newName}' across {changedDocs.Count} file(s)");
        sb.AppendLine();
        sb.AppendLine("Changed files:");

        foreach (var (filePath, _) in changedDocs.OrderBy(d => d.FilePath))
        {
            var displayPath = LocationFormatter.MakePathRelative(filePath, solutionDir);

            if (oldFilePath != null && newFilePath != null &&
                string.Equals(filePath, oldFilePath, StringComparison.OrdinalIgnoreCase))
            {
                var newDisplayPath = LocationFormatter.MakePathRelative(newFilePath, solutionDir);
                sb.AppendLine($"  {displayPath} → {newDisplayPath} (file renamed)");
            }
            else
            {
                sb.AppendLine($"  {displayPath}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
