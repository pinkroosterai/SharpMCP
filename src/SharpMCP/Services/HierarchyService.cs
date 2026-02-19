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

    public async Task<List<SymbolResult>> FindImplementationsAsync(string solutionPath, string interfaceName, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var interfaceSymbol = await _symbolResolver.ResolveTypeAsync(solutionPath, interfaceName);

        if (interfaceSymbol.TypeKind != TypeKind.Interface)
            throw new ArgumentException($"'{interfaceName}' is not an interface (it's a {interfaceSymbol.TypeKind}).");

        var implementations = await SymbolFinder.FindImplementationsAsync(interfaceSymbol, solution);
        var results = new List<SymbolResult>();

        foreach (var impl in implementations.OfType<INamedTypeSymbol>())
        {
            results.Add(BuildSymbolResult(impl, solutionDir));
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    public async Task<List<SymbolResult>> FindSubclassesAsync(string solutionPath, string baseClassName, DetailLevel detail = DetailLevel.Compact)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var baseClass = await _symbolResolver.ResolveTypeAsync(solutionPath, baseClassName);

        if (baseClass.TypeKind != TypeKind.Class)
            throw new ArgumentException($"'{baseClassName}' is not a class (it's a {baseClass.TypeKind}).");

        var derived = await SymbolFinder.FindDerivedClassesAsync(baseClass, solution);
        var results = new List<SymbolResult>();

        foreach (var type in derived)
        {
            results.Add(BuildSymbolResult(type, solutionDir));
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
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
            results.Add(BuildSymbolResult(@override, solutionDir));
        }

        return results.OrderBy(r => r.FilePath).ThenBy(r => r.Line).ToList();
    }

    private static SymbolResult BuildSymbolResult(ISymbol symbol, string solutionDir) =>
        SymbolFormatter.BuildSymbolResult(symbol, solutionDir);
}
