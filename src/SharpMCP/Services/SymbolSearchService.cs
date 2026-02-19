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

    public async Task<List<SymbolResult>> FindSymbolsAsync(
        string solutionPath, string query, string? kind = null,
        bool exact = false, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var results = new List<SymbolResult>();
        var seen = new HashSet<string>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var symbols = exact
                ? compilation.GetSymbolsWithName(query, SymbolFilter.All)
                : compilation.GetSymbolsWithName(
                    name => name.Contains(query, StringComparison.OrdinalIgnoreCase),
                    SymbolFilter.All);

            foreach (var symbol in symbols)
            {
                if (!symbol.Locations.Any(l => l.IsInSource))
                    continue;

                if (kind != null && !string.Equals(SymbolFormatter.GetKindString(symbol), kind, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (symbol.IsImplicitlyDeclared)
                    continue;

                var key = $"{symbol.ToDisplayString()}|{symbol.Kind}";
                if (!seen.Add(key))
                    continue;

                var result = SymbolFormatter.BuildSymbolResult(symbol, solutionDir);

                if (detail == DetailLevel.Full)
                {
                    var docComment = SymbolFormatter.ExtractSummary(symbol.GetDocumentationCommentXml());
                    var sourceBody = await SymbolFormatter.GetSourceBodyAsync(symbol);
                    result = result with { DocComment = docComment, SourceBody = sourceBody };
                }

                results.Add(result);
            }
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    public async Task<List<SymbolResult>> GetFileSymbolsAsync(
        string solutionPath, string filePath, int depth = 0, DetailLevel detail = DetailLevel.Compact)
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

            var typeResult = SymbolFormatter.BuildSymbolResult(typeSymbol, solutionDir);
            if (detail == DetailLevel.Full)
            {
                var docComment = SymbolFormatter.ExtractSummary(typeSymbol.GetDocumentationCommentXml());
                var sourceBody = await SymbolFormatter.GetSourceBodyAsync(typeSymbol);
                typeResult = typeResult with { DocComment = docComment, SourceBody = sourceBody };
            }
            results.Add(typeResult);

            if (depth > 0)
            {
                foreach (var member in typeSymbol.GetMembers())
                {
                    if (member.IsImplicitlyDeclared) continue;
                    var memberResult = SymbolFormatter.BuildSymbolResult(member, solutionDir);
                    if (detail == DetailLevel.Full)
                    {
                        var docComment = SymbolFormatter.ExtractSummary(member.GetDocumentationCommentXml());
                        var sourceBody = await SymbolFormatter.GetSourceBodyAsync(member);
                        memberResult = memberResult with { DocComment = docComment, SourceBody = sourceBody };
                    }
                    results.Add(memberResult);
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
