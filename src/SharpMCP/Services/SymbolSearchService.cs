using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpMCP.Formatting;
using SharpMCP.Models;

namespace SharpMCP.Services;

public sealed class SymbolSearchService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SymbolResolver _symbolResolver;

    public SymbolSearchService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
    {
        _workspaceManager = workspaceManager;
        _symbolResolver = symbolResolver;
    }

    public async Task<List<SymbolResult>> FindSymbolsAsync(string solutionPath, string query, string? kind = null)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var results = new List<SymbolResult>();
        var seen = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var symbols = compilation.GetSymbolsWithName(
                name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.All);

            foreach (var symbol in symbols)
            {
                if (kind != null && !string.Equals(SymbolFormatter.GetKindString(symbol), kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (symbol.IsImplicitlyDeclared)
                    continue;

                var key = $"{symbol.ToDisplayString()}|{symbol.Kind}";
                if (!seen.Add(key))
                    continue;

                results.Add(SymbolFormatter.BuildSymbolResult(symbol, solutionDir));
            }
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    public async Task<List<SymbolResult>> GetFileSymbolsAsync(string solutionPath, string filePath, int depth = 0)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var absolutePath = Path.IsPathRooted(filePath) ? filePath : Path.GetFullPath(Path.Combine(solutionDir, filePath));

        Document? document = null;
        foreach (var project in solution.Projects)
        {
            document = project.Documents.FirstOrDefault(
                d => string.Equals(d.FilePath, absolutePath, StringComparison.OrdinalIgnoreCase));
            if (document != null) break;
        }

        if (document == null)
            throw new FileNotFoundException($"File not found in any project: {filePath}");

        var root = await document.GetSyntaxRootAsync()
            ?? throw new InvalidOperationException("Could not get syntax root.");
        var semanticModel = await document.GetSemanticModelAsync()
            ?? throw new InvalidOperationException("Could not get semantic model.");

        var results = new List<SymbolResult>();

        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol)
                continue;

            results.Add(SymbolFormatter.BuildSymbolResult(typeSymbol, solutionDir));

            if (depth > 0)
            {
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member.IsImplicitlyDeclared) continue;
                    results.Add(SymbolFormatter.BuildSymbolResult(member, solutionDir));
                }
            }
        }

        return results;
    }

    public async Task<List<SymbolResult>> GetTypeMembersAsync(string solutionPath, string typeName, DetailLevel detail = DetailLevel.Compact)
    {
        var type = await _symbolResolver.ResolveTypeAsync(solutionPath, typeName);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var results = new List<SymbolResult>();

        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            if (member.Name.StartsWith("<")) continue;

            var result = SymbolFormatter.BuildSymbolResult(member, solutionDir);

            if (detail == DetailLevel.Full)
            {
                var docComment = SymbolFormatter.ExtractSummary(member.GetDocumentationCommentXml());
                var sourceBody = await SymbolFormatter.GetSourceBodyAsync(member);
                result = result with { DocComment = docComment, SourceBody = sourceBody };
            }

            results.Add(result);
        }

        return results;
    }

    public async Task<SymbolResult?> GetSymbolInfoAsync(string solutionPath, string symbolName, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        ISymbol? found = null;
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var symbols = compilation.GetSymbolsWithName(symbolName, SymbolFilter.All);
            found = symbols.FirstOrDefault(s => s.Locations.Any(l => l.IsInSource));
            if (found != null) break;
        }

        if (found == null)
            return null;

        var result = SymbolFormatter.BuildSymbolResult(found, solutionDir);

        if (detail == DetailLevel.Full)
        {
            var docComment = SymbolFormatter.ExtractSummary(found.GetDocumentationCommentXml());
            var sourceBody = await SymbolFormatter.GetSourceBodyAsync(found);
            result = result with { DocComment = docComment, SourceBody = sourceBody };
        }

        return result;
    }

    public async Task<List<string>> ListNamespacesAsync(string solutionPath)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var namespaces = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
            {
                // Only include namespaces from source-defined types
                if (!type.Locations.Any(l => l.IsInSource))
                    continue;

                var ns = type.ContainingNamespace?.ToDisplayString();
                if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                    namespaces.Add(ns);
            }
        }

        return namespaces.OrderBy(n => n).ToList();
    }
}
