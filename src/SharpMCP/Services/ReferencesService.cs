using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpMCP.Formatting;
using SharpMCP.Models;

namespace SharpMCP.Services;

public sealed class ReferencesService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SymbolResolver _symbolResolver;

    public ReferencesService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
    {
        _workspaceManager = workspaceManager;
        _symbolResolver = symbolResolver;
    }

    public async Task<List<ReferenceResult>> FindReferencesAsync(
        string solutionPath, string symbolName, string? typeName = null,
        string? projectScope = null, DetailLevel detail = DetailLevel.Compact)
    {
        var symbol = await _symbolResolver.ResolveSymbolAsync(solutionPath, symbolName, typeName);
        return await FindReferencesForSymbolAsync(solutionPath, symbol, projectScope, detail);
    }

    private async Task<List<ReferenceResult>> FindReferencesForSymbolAsync(
        string solutionPath, ISymbol symbol,
        string? projectScope = null, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var references = await SymbolFinder.FindReferencesAsync(symbol, solution);
        var results = new List<ReferenceResult>();

        foreach (var referencedSymbol in references)
        {
            foreach (var location in referencedSymbol.Locations)
            {
                var document = location.Document;

                if (projectScope != null &&
                    !string.Equals(document.Project.Name, projectScope, StringComparison.OrdinalIgnoreCase))
                    continue;

                var filePath = document.FilePath ?? "(unknown)";
                var displayPath = LocationFormatter.MakePathRelative(filePath, solutionDir);
                var lineSpan = location.Location.GetLineSpan();
                var line = lineSpan.StartLinePosition.Line + 1;
                var column = lineSpan.StartLinePosition.Character + 1;

                var sourceText = await document.GetTextAsync();
                var snippet = GetLineText(sourceText, lineSpan.StartLinePosition.Line);

                string? contextBefore = null;
                string? contextAfter = null;
                string? containingSymbol = null;

                if (detail == DetailLevel.Full)
                {
                    contextBefore = GetContextLines(sourceText, lineSpan.StartLinePosition.Line, -2);
                    contextAfter = GetContextLines(sourceText, lineSpan.StartLinePosition.Line, 2);
                }

                // Try to find the containing symbol
                var root = await document.GetSyntaxRootAsync();
                var semanticModel = await document.GetSemanticModelAsync();
                if (root != null && semanticModel != null)
                {
                    var node = root.FindNode(location.Location.SourceSpan);
                    var enclosing = semanticModel.GetEnclosingSymbol(location.Location.SourceSpan.Start);
                    if (enclosing != null)
                        containingSymbol = enclosing.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                }

                results.Add(new ReferenceResult(
                    FilePath: displayPath,
                    Line: line,
                    Column: column,
                    CodeSnippet: snippet.Trim(),
                    ContextBefore: contextBefore,
                    ContextAfter: contextAfter,
                    ContainingSymbol: containingSymbol
                ));
            }
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    public async Task<List<ReferenceResult>> FindCallersAsync(
        string solutionPath, string methodName, string? typeName = null, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var method = await _symbolResolver.ResolveMethodAsync(solutionPath, methodName, typeName);

        var callers = await SymbolFinder.FindCallersAsync(method, solution);
        var results = new List<ReferenceResult>();

        foreach (var caller in callers)
        {
            foreach (var location in caller.Locations)
            {
                if (!location.IsInSource) continue;

                var filePath = location.SourceTree?.FilePath ?? "(unknown)";
                var displayPath = LocationFormatter.MakePathRelative(filePath, solutionDir);
                var lineSpan = location.GetLineSpan();
                var line = lineSpan.StartLinePosition.Line + 1;
                var column = lineSpan.StartLinePosition.Character + 1;

                var sourceText = location.SourceTree?.GetText();
                var snippet = sourceText != null
                    ? GetLineText(sourceText, lineSpan.StartLinePosition.Line)
                    : "";

                string? contextBefore = null;
                string? contextAfter = null;

                if (detail == DetailLevel.Full && sourceText != null)
                {
                    contextBefore = GetContextLines(sourceText, lineSpan.StartLinePosition.Line, -2);
                    contextAfter = GetContextLines(sourceText, lineSpan.StartLinePosition.Line, 2);
                }

                var callerName = caller.CallingSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                results.Add(new ReferenceResult(
                    FilePath: displayPath,
                    Line: line,
                    Column: column,
                    CodeSnippet: snippet.Trim(),
                    ContextBefore: contextBefore,
                    ContextAfter: contextAfter,
                    ContainingSymbol: callerName
                ));
            }
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    public async Task<List<ReferenceResult>> FindUsagesAsync(
        string solutionPath, string typeName, DetailLevel detail = DetailLevel.Compact)
    {
        var typeSymbol = await _symbolResolver.ResolveTypeAsync(solutionPath, typeName);
        return await FindReferencesForSymbolAsync(solutionPath, typeSymbol, detail: detail);
    }

    private static string GetLineText(Microsoft.CodeAnalysis.Text.SourceText sourceText, int lineNumber)
    {
        if (lineNumber >= 0 && lineNumber < sourceText.Lines.Count)
            return sourceText.Lines[lineNumber].ToString();
        return "";
    }

    private static string? GetContextLines(Microsoft.CodeAnalysis.Text.SourceText sourceText, int centerLine, int offset)
    {
        var lines = new List<string>();
        var start = offset < 0 ? centerLine + offset : centerLine + 1;
        var end = offset < 0 ? centerLine : centerLine + offset + 1;

        for (int i = Math.Max(0, start); i < Math.Min(sourceText.Lines.Count, end); i++)
        {
            if (i == centerLine) continue;
            lines.Add(sourceText.Lines[i].ToString());
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }
}
