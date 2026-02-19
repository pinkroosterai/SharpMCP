using Microsoft.CodeAnalysis;

namespace SharpMCP.Services;

public sealed class SymbolResolver
{
    private readonly WorkspaceManager _workspaceManager;

    public SymbolResolver(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<INamedTypeSymbol> ResolveTypeAsync(string solutionPath, string typeName)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var candidates = new List<INamedTypeSymbol>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (string.Equals(type.Name, typeName, StringComparison.Ordinal)
                    || string.Equals(type.ToDisplayString(), typeName, StringComparison.Ordinal))
                {
                    // Avoid duplicates from project references
                    if (!candidates.Any(c => SymbolEqualityComparer.Default.Equals(c, type)))
                        candidates.Add(type);
                }
            }
        }

        return candidates.Count switch
        {
            0 => throw new KeyNotFoundException($"Type '{typeName}' not found."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Ambiguous type '{typeName}'. Found:\n" +
                string.Join("\n", candidates.Select(c => $"  - {c.ToDisplayString()} [{c.Locations.FirstOrDefault()?.SourceTree?.FilePath}]")))
        };
    }

    public async Task<ISymbol> ResolveSymbolAsync(string solutionPath, string symbolName, string? containingType = null)
    {
        if (containingType != null)
        {
            var type = await ResolveTypeAsync(solutionPath, containingType);
            var member = type.GetMembers()
                .FirstOrDefault(m => string.Equals(m.Name, symbolName, StringComparison.Ordinal));

            if (member == null)
                throw new KeyNotFoundException(
                    $"Member '{symbolName}' not found in type '{containingType}'.");

            return member;
        }

        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var candidates = new List<ISymbol>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var symbols = compilation.GetSymbolsWithName(symbolName, SymbolFilter.All);
            foreach (var s in symbols)
            {
                if (!candidates.Any(c => SymbolEqualityComparer.Default.Equals(c, s)))
                    candidates.Add(s);
            }
        }

        return candidates.Count switch
        {
            0 => throw new KeyNotFoundException($"Symbol '{symbolName}' not found."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Ambiguous symbol '{symbolName}'. Found:\n" +
                string.Join("\n", candidates.Select(c => $"  - {c.ToDisplayString()} ({c.Kind}) [{c.Locations.FirstOrDefault()?.SourceTree?.FilePath}]")))
        };
    }

    public async Task<IMethodSymbol> ResolveMethodAsync(string solutionPath, string methodName, string? typeName = null)
    {
        if (typeName != null)
        {
            var type = await ResolveTypeAsync(solutionPath, typeName);
            var methods = type.GetMembers(methodName).OfType<IMethodSymbol>().ToList();

            if (methods.Count == 0)
                throw new KeyNotFoundException(
                    $"Method '{methodName}' not found in type '{typeName}'.");

            if (methods.Count > 1)
            {
                Console.Error.WriteLine(
                    $"Warning: '{methodName}' in '{typeName}' has {methods.Count} overloads. " +
                    $"Using: {methods[0].ToDisplayString()}");
            }

            return methods[0];
        }

        var symbol = await ResolveSymbolAsync(solutionPath, methodName);
        if (symbol is IMethodSymbol method)
            return method;

        throw new InvalidOperationException($"Symbol '{methodName}' is not a method (it's a {symbol.Kind}).");
    }

    public static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(Compilation compilation)
    {
        return GetAllNamedTypes(compilation.GlobalNamespace);
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in GetNestedTypes(type))
                yield return nested;
        }

        foreach (var ns in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in GetAllNamedTypes(ns))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deepNested in GetNestedTypes(nested))
                yield return deepNested;
        }
    }
}
