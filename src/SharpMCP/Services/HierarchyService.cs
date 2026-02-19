using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpMCP.Formatting;
using SharpMCP.Models;

namespace SharpMCP.Services;

public sealed class HierarchyService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SymbolResolver _symbolResolver;

    public HierarchyService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
    {
        _workspaceManager = workspaceManager;
        _symbolResolver = symbolResolver;
    }

    public async Task<(List<SymbolResult> Results, string TypeKind)> FindDerivedTypesAsync(
        string solutionPath, string typeName, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var resolvedType = await _symbolResolver.ResolveTypeAsync(solutionPath, typeName);

        IEnumerable<INamedTypeSymbol> derived;
        string typeKind;

        if (resolvedType.TypeKind == TypeKind.Interface)
        {
            var implementations = await SymbolFinder.FindImplementationsAsync(resolvedType, solution);
            derived = implementations.OfType<INamedTypeSymbol>();
            typeKind = "interface";
        }
        else if (resolvedType.TypeKind == TypeKind.Class)
        {
            derived = await SymbolFinder.FindDerivedClassesAsync(resolvedType, solution);
            typeKind = "class";
        }
        else
        {
            throw new ArgumentException($"'{typeName}' is not an interface or class (it's a {resolvedType.TypeKind}).");
        }

        var results = new List<SymbolResult>();
        foreach (var symbol in derived)
        {
            if (!symbol.Locations.Any(l => l.IsInSource))
                continue;
            results.Add(BuildSymbolResult(symbol, solutionDir));
        }

        return (results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList(), typeKind);
    }

    public async Task<TypeHierarchyResult> GetTypeHierarchyAsync(string solutionPath, string typeName)
    {
        var type = await _symbolResolver.ResolveTypeAsync(solutionPath, typeName);

        var baseTypes = new List<string>();
        var current = type.BaseType;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            baseTypes.Add(current.ToDisplayString());
            current = current.BaseType;
        }
        if (current?.SpecialType == SpecialType.System_Object)
            baseTypes.Add("object");

        var interfaces = type.AllInterfaces
            .Select(i => i.ToDisplayString())
            .OrderBy(n => n)
            .ToList();

        var kind = type.TypeKind.ToString().ToLowerInvariant();

        return new TypeHierarchyResult(
            TypeName: type.ToDisplayString(),
            Kind: kind,
            BaseTypes: baseTypes,
            Interfaces: interfaces
        );
    }

    public async Task<List<SymbolResult>> FindOverridesAsync(string solutionPath, string typeName, string methodName, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var method = await _symbolResolver.ResolveMethodAsync(solutionPath, methodName, typeName);

        if (!method.IsVirtual && !method.IsAbstract && !method.IsOverride)
            throw new ArgumentException($"Method '{methodName}' in '{typeName}' is not virtual, abstract, or override.");

        var overrides = await SymbolFinder.FindOverridesAsync(method, solution);
        var results = new List<SymbolResult>();

        foreach (var @override in overrides)
        {
            if (!@override.Locations.Any(l => l.IsInSource))
                continue;

            results.Add(BuildSymbolResult(@override, solutionDir));
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    private static SymbolResult BuildSymbolResult(ISymbol symbol, string solutionDir) =>
        SymbolFormatter.BuildSymbolResult(symbol, solutionDir);
}
