using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpMCP.Formatting;

namespace SharpMCP.Services;

public sealed class AnalysisService
{
    private readonly WorkspaceManager _workspaceManager;

    public AnalysisService(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<string> FindUnusedCodeAsync(
        string solutionPath, string scope = "all", string? projectName = null)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var projects = projectName != null
            ? solution.Projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToList()
            : solution.Projects.ToList();

        if (projects.Count == 0 && projectName != null)
            throw new KeyNotFoundException($"Project '{projectName}' not found.");

        var unusedTypes = new List<(ISymbol Symbol, string FilePath, int Line)>();
        var unusedMethods = new List<(ISymbol Symbol, string FilePath, int Line)>();
        var unusedProperties = new List<(ISymbol Symbol, string FilePath, int Line)>();
        var unusedFields = new List<(ISymbol Symbol, string FilePath, int Line)>();

        var wantTypes = scope is "all" or "types";
        var wantMethods = scope is "all" or "methods";
        var wantProperties = scope is "all" or "properties";
        var wantFields = scope is "all" or "fields";

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
            {
                if (!type.Locations.Any(l => l.IsInSource))
                    continue;

                // Check the type itself
                if (wantTypes && ShouldCheckType(type, project))
                {
                    if (await IsUnreferencedAsync(type, solution))
                        AddToList(unusedTypes, type, solutionDir);
                }

                // Check members
                foreach (var member in type.GetMembers())
                {
                    if (!member.Locations.Any(l => l.IsInSource))
                        continue;
                    if (member.IsImplicitlyDeclared)
                        continue;

                    switch (member)
                    {
                        case IMethodSymbol method when wantMethods:
                            if (ShouldCheckMethod(method))
                            {
                                if (await IsUnreferencedAsync(method, solution))
                                    AddToList(unusedMethods, method, solutionDir);
                            }
                            break;

                        case IPropertySymbol property when wantProperties:
                            if (ShouldCheckMember(property))
                            {
                                if (await IsUnreferencedAsync(property, solution))
                                    AddToList(unusedProperties, property, solutionDir);
                            }
                            break;

                        case IFieldSymbol field when wantFields:
                            if (ShouldCheckField(field))
                            {
                                if (await IsUnreferencedAsync(field, solution))
                                    AddToList(unusedFields, field, solutionDir);
                            }
                            break;
                    }
                }
            }
        }

        return FormatResults(unusedTypes, unusedMethods, unusedProperties, unusedFields,
            projectName ?? Path.GetFileNameWithoutExtension(solutionPath), scope);
    }

    private static bool ShouldCheckType(INamedTypeSymbol type, Project project)
    {
        // Skip entry point types (Program class with Main or top-level statements)
        if (type.Name == "Program" || type.Name == "<Program>$")
            return false;

        // Skip types with special attributes
        if (HasExcludedAttribute(type))
            return false;

        // Skip public types in library projects (OutputType != Exe)
        if (type.DeclaredAccessibility == Accessibility.Public)
        {
            // Conservative: skip public types since they might be consumed externally
            // Only flag internal/private types as unused
            return false;
        }

        return true;
    }

    private static bool ShouldCheckMethod(IMethodSymbol method)
    {
        // Skip special methods
        if (method.MethodKind is not MethodKind.Ordinary)
            return false;

        // Skip Main entry points
        if (method.Name == "Main")
            return false;

        // Skip interface implementations
        if (IsInterfaceImplementation(method))
            return false;

        // Skip overrides (called via base class dispatch)
        if (method.IsOverride)
            return false;

        // Skip methods with excluded attributes
        if (HasExcludedAttribute(method))
            return false;

        return ShouldCheckMember(method);
    }

    private static bool ShouldCheckMember(ISymbol member)
    {
        // Only check private and internal members
        if (member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected
            or Accessibility.ProtectedOrInternal)
            return false;

        if (HasExcludedAttribute(member))
            return false;

        return true;
    }

    private static bool ShouldCheckField(IFieldSymbol field)
    {
        // Skip backing fields
        if (field.IsImplicitlyDeclared)
            return false;

        // Skip constants used in attributes (common pattern)
        if (field.IsConst && field.DeclaredAccessibility == Accessibility.Public)
            return false;

        return ShouldCheckMember(field);
    }

    private static bool IsInterfaceImplementation(ISymbol member)
    {
        if (member.ContainingType == null)
            return false;

        foreach (var iface in member.ContainingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers())
            {
                var impl = member.ContainingType.FindImplementationForInterfaceMember(ifaceMember);
                if (impl != null && SymbolEqualityComparer.Default.Equals(impl, member))
                    return true;
            }
        }

        return false;
    }

    internal static readonly HashSet<string> ExcludedAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "Test", "TestMethod", "TestCase",
        "McpServerTool", "McpServerToolType",
        "ApiController", "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch",
        "JsonConverter", "JsonPropertyName",
        "Obsolete", "ExportAttribute", "CompositionExport",
        "Serializable"
    };

    internal static bool HasExcludedAttribute(ISymbol symbol)
    {
        return symbol.GetAttributes().Any(attr =>
        {
            var name = attr.AttributeClass?.Name;
            if (name == null) return false;
            // Check both "FooAttribute" and "Foo" forms
            return ExcludedAttributes.Contains(name)
                || ExcludedAttributes.Contains(name.Replace("Attribute", ""));
        });
    }

    private static async Task<bool> IsUnreferencedAsync(ISymbol symbol, Solution solution)
    {
        var references = await SymbolFinder.FindReferencesAsync(symbol, solution);
        foreach (var refGroup in references)
        {
            if (refGroup.Locations.Any())
                return false;
        }
        return true;
    }

    private static void AddToList(
        List<(ISymbol Symbol, string FilePath, int Line)> list,
        ISymbol symbol, string solutionDir)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location == null) return;

        var filePath = location.SourceTree?.FilePath;
        if (filePath == null) return;

        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        var displayPath = LocationFormatter.MakePathRelative(filePath, solutionDir);
        list.Add((symbol, displayPath, line));
    }

    private static string FormatResults(
        List<(ISymbol Symbol, string FilePath, int Line)> types,
        List<(ISymbol Symbol, string FilePath, int Line)> methods,
        List<(ISymbol Symbol, string FilePath, int Line)> properties,
        List<(ISymbol Symbol, string FilePath, int Line)> fields,
        string scopeName, string scope)
    {
        var total = types.Count + methods.Count + properties.Count + fields.Count;

        if (total == 0)
            return $"No unused code found in {scopeName} (scope: {scope}).\nNote: public and protected symbols are excluded from analysis (they may be consumed externally).";

        var sb = new StringBuilder();
        sb.AppendLine($"Unused code in {scopeName} ({total} symbol(s)):");

        if (scope is "all" or "types")
        {
            sb.AppendLine("  Types:");
            if (types.Count == 0)
                sb.AppendLine("    (none)");
            else
                foreach (var (sym, path, line) in types)
                    sb.AppendLine($"    {sym.DeclaredAccessibility.ToString().ToLowerInvariant()} {sym.ToDisplayString()}  [{path}:{line}]");
        }

        if (scope is "all" or "methods")
        {
            sb.AppendLine("  Methods:");
            if (methods.Count == 0)
                sb.AppendLine("    (none)");
            else
                foreach (var (sym, path, line) in methods)
                    sb.AppendLine($"    {FormatMemberDisplay(sym)}  [{path}:{line}]");
        }

        if (scope is "all" or "properties")
        {
            sb.AppendLine("  Properties:");
            if (properties.Count == 0)
                sb.AppendLine("    (none)");
            else
                foreach (var (sym, path, line) in properties)
                    sb.AppendLine($"    {FormatMemberDisplay(sym)}  [{path}:{line}]");
        }

        if (scope is "all" or "fields")
        {
            sb.AppendLine("  Fields:");
            if (fields.Count == 0)
                sb.AppendLine("    (none)");
            else
                foreach (var (sym, path, line) in fields)
                    sb.AppendLine($"    {FormatMemberDisplay(sym)}  [{path}:{line}]");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatMemberDisplay(ISymbol symbol)
    {
        var access = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
        return symbol switch
        {
            IMethodSymbol method =>
                $"{access} {method.ReturnType.ToDisplayString()} {method.ContainingType.Name}.{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString()))})",
            IPropertySymbol property =>
                $"{access} {property.Type.ToDisplayString()} {property.ContainingType.Name}.{property.Name}",
            IFieldSymbol field =>
                $"{access} {field.Type.ToDisplayString()} {field.ContainingType.Name}.{field.Name}",
            _ => $"{access} {symbol.ToDisplayString()}"
        };
    }

    // ──────────────────────────────────────────────
    // find_code_smells orchestration
    // ──────────────────────────────────────────────

    public async Task<string> FindCodeSmellsAsync(
        string solutionPath, string category = "all",
        string? projectName = null, bool deep = false)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var projects = projectName != null
            ? solution.Projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)).ToList()
            : solution.Projects.ToList();

        if (projects.Count == 0 && projectName != null)
            throw new KeyNotFoundException($"Project '{projectName}' not found.");

        var wantComplexity = category is "all" or "complexity";
        var wantDesign = category is "all" or "design";
        var wantInheritance = category is "all" or "inheritance";

        var allSmells = new List<SmellResult>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            // Method body analysis (covers complexity + middle man)
            if (wantComplexity || wantDesign)
            {
                var bodySmells = await CodeSmellChecks.CheckMethodBodySmellsAsync(compilation, solutionDir);
                if (wantComplexity)
                    allSmells.AddRange(bodySmells.Where(s =>
                        s.SmellName is "Long method" or "Deep nesting" or "High complexity"));
                if (wantDesign)
                    allSmells.AddRange(bodySmells.Where(s => s.SmellName == "Middle man"));
            }

            // Complexity: symbol-level
            if (wantComplexity)
            {
                allSmells.AddRange(CodeSmellChecks.CheckLargeClasses(compilation, solutionDir));
                allSmells.AddRange(CodeSmellChecks.CheckLongParameterLists(compilation, solutionDir));
            }

            // Design: symbol-level
            if (wantDesign)
            {
                allSmells.AddRange(CodeSmellChecks.CheckGodClasses(compilation, solutionDir));
                allSmells.AddRange(CodeSmellChecks.CheckDataClasses(compilation, solutionDir));
                allSmells.AddRange(CodeSmellChecks.CheckTooManyDependencies(compilation, solutionDir));

                if (deep)
                    allSmells.AddRange(await CodeSmellChecks.CheckFeatureEnvyAsync(
                        compilation, solution, project, solutionDir));
            }

            // Inheritance
            if (wantInheritance)
            {
                allSmells.AddRange(CodeSmellChecks.CheckDeepInheritance(compilation, solutionDir));
                allSmells.AddRange(CodeSmellChecks.CheckRefusedBequest(compilation, solutionDir));
                allSmells.AddRange(CodeSmellChecks.CheckSpeculativeGenerality(compilation, solutionDir));
            }
        }

        var scopeName = projectName ?? Path.GetFileNameWithoutExtension(solutionPath);
        return FormatSmellResults(allSmells, scopeName, category);
    }

    private static string FormatSmellResults(
        List<SmellResult> smells, string scopeName, string category)
    {
        if (smells.Count == 0)
            return $"No code smells found in {scopeName} (category: {category}).";

        var sb = new StringBuilder();
        sb.AppendLine($"Code smells in {scopeName} ({smells.Count} issue(s)):");

        var severityOrder = new[] { "critical", "warning", "info" };
        foreach (var severity in severityOrder)
        {
            var group = smells.Where(s => s.Severity == severity).ToList();
            if (group.Count == 0) continue;

            sb.AppendLine();
            sb.AppendLine($"{char.ToUpper(severity[0])}{severity[1..]} ({group.Count}):");

            foreach (var smellGroup in group.GroupBy(s => s.SmellName))
            {
                var header = FormatSmellHeader(smellGroup.Key, severity);
                sb.AppendLine($"  {header}:");
                foreach (var smell in smellGroup)
                    sb.AppendLine($"    {smell.SymbolName} ({smell.Detail})  [{smell.FilePath}:{smell.Line}]");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSmellHeader(string smellName, string severity)
    {
        return (smellName, severity) switch
        {
            ("Long method", "critical") => "Long method (>100 lines)",
            ("Long method", "warning") => "Long method (>50 lines)",
            ("Large class", "critical") => "Large class (>40 members)",
            ("Large class", "warning") => "Large class (>20 members)",
            ("Long parameter list", "critical") => "Long parameter list (>8)",
            ("Long parameter list", "warning") => "Long parameter list (>5)",
            ("Deep nesting", "critical") => "Deep nesting (>5 levels)",
            ("Deep nesting", "warning") => "Deep nesting (>3 levels)",
            ("High complexity", "critical") => "High complexity (>20)",
            ("High complexity", "warning") => "High complexity (>10)",
            ("Too many dependencies", "critical") => "Too many dependencies (>8)",
            ("Too many dependencies", "warning") => "Too many dependencies (>5)",
            _ => smellName
        };
    }
}
