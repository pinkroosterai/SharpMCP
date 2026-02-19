using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpMCP.Formatting;

namespace SharpMCP.Services;

internal record SmellResult(
    string SmellName,
    string Severity,
    string SymbolName,
    string Detail,
    string FilePath,
    int Line);

internal record MethodBodyMetrics(
    int LineCount,
    int MaxNestingDepth,
    int CyclomaticComplexity,
    bool IsSingleDelegation);

internal static class CodeSmellChecks
{
    // ──────────────────────────────────────────────
    // Shared helpers
    // ──────────────────────────────────────────────

    private static bool ShouldAnalyzeType(INamedTypeSymbol type)
    {
        if (!type.Locations.Any(l => l.IsInSource))
            return false;
        if (type.IsImplicitlyDeclared)
            return false;
        if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate or TypeKind.Interface)
            return false;
        if (type.Name is "Program" or "<Program>$")
            return false;
        if (AnalysisService.HasExcludedAttribute(type))
            return false;
        return true;
    }

    private static (string FilePath, int Line) GetLocation(ISymbol symbol, string solutionDir)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree == null)
            return ("(unknown)", 0);

        var filePath = LocationFormatter.MakePathRelative(location.SourceTree.FilePath, solutionDir);
        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        return (filePath, line);
    }

    private static SmellResult MakeResult(
        string smellName, string severity, ISymbol symbol, string detail, string solutionDir)
    {
        var (filePath, line) = GetLocation(symbol, solutionDir);
        var symbolName = symbol is INamedTypeSymbol
            ? symbol.Name
            : $"{symbol.ContainingType?.Name}.{symbol.Name}";
        return new SmellResult(smellName, severity, symbolName, detail, filePath, line);
    }

    // ──────────────────────────────────────────────
    // Method body analysis (single-pass)
    // ──────────────────────────────────────────────

    private static async Task<MethodBodyMetrics?> AnalyzeMethodBodyAsync(IMethodSymbol method)
    {
        var syntaxRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;

        var node = await syntaxRef.GetSyntaxAsync();

        SyntaxNode? body = node switch
        {
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
            _ => null
        };

        if (body == null) return null;

        var lineCount = body is ArrowExpressionClauseSyntax
            ? 1
            : body.GetText().Lines.Count;
        var maxNesting = ComputeMaxNesting(body);
        var complexity = ComputeCyclomaticComplexity(body);
        var isSingleDelegation = CheckSingleDelegation(body);

        return new MethodBodyMetrics(lineCount, maxNesting, complexity, isSingleDelegation);
    }

    private static int ComputeMaxNesting(SyntaxNode node)
    {
        int maxDepth = 0;
        foreach (var child in node.ChildNodes())
        {
            bool isNesting = child is IfStatementSyntax
                or ForStatementSyntax or ForEachStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax
                or SwitchStatementSyntax or TryStatementSyntax;

            int childDepth = ComputeMaxNesting(child);
            if (isNesting)
                childDepth += 1;

            maxDepth = Math.Max(maxDepth, childDepth);
        }
        return maxDepth;
    }

    private static int ComputeCyclomaticComplexity(SyntaxNode body)
    {
        int count = 1;
        foreach (var node in body.DescendantNodes())
        {
            count += node switch
            {
                IfStatementSyntax => 1,
                CaseSwitchLabelSyntax => 1,
                CasePatternSwitchLabelSyntax => 1,
                SwitchExpressionArmSyntax => 1,
                ConditionalExpressionSyntax => 1,
                CatchClauseSyntax => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => 1,
                BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression) => 1,
                _ => 0
            };
        }
        return count;
    }

    private static bool CheckSingleDelegation(SyntaxNode body)
    {
        if (body is not BlockSyntax block)
            return false;
        if (block.Statements.Count != 1)
            return false;

        var stmt = block.Statements[0];
        return stmt switch
        {
            ExpressionStatementSyntax es => es.Expression is InvocationExpressionSyntax,
            ReturnStatementSyntax rs => rs.Expression is InvocationExpressionSyntax,
            _ => false
        };
    }

    // ──────────────────────────────────────────────
    // Combined method body check (4 smells, 1 pass)
    // ──────────────────────────────────────────────

    public static async Task<List<SmellResult>> CheckMethodBodySmellsAsync(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();

        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;

            int methodCount = 0;
            int delegationCount = 0;

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsImplicitlyDeclared) continue;
                if (method.MethodKind != MethodKind.Ordinary) continue;

                methodCount++;
                var metrics = await AnalyzeMethodBodyAsync(method);
                if (metrics == null) continue;

                // Long method
                if (metrics.LineCount > 100)
                    results.Add(MakeResult("Long method", "critical", method,
                        $"{metrics.LineCount} lines", solutionDir));
                else if (metrics.LineCount > 50)
                    results.Add(MakeResult("Long method", "warning", method,
                        $"{metrics.LineCount} lines", solutionDir));

                // Deep nesting
                if (metrics.MaxNestingDepth > 5)
                    results.Add(MakeResult("Deep nesting", "critical", method,
                        $"{metrics.MaxNestingDepth} levels", solutionDir));
                else if (metrics.MaxNestingDepth > 3)
                    results.Add(MakeResult("Deep nesting", "warning", method,
                        $"{metrics.MaxNestingDepth} levels", solutionDir));

                // Cyclomatic complexity
                if (metrics.CyclomaticComplexity > 20)
                    results.Add(MakeResult("High complexity", "critical", method,
                        $"complexity: {metrics.CyclomaticComplexity}", solutionDir));
                else if (metrics.CyclomaticComplexity > 10)
                    results.Add(MakeResult("High complexity", "warning", method,
                        $"complexity: {metrics.CyclomaticComplexity}", solutionDir));

                // Track for middle man
                if (metrics.IsSingleDelegation)
                    delegationCount++;
            }

            // Middle man check (per type)
            if (methodCount >= 3 && delegationCount > methodCount * 0.8)
                results.Add(MakeResult("Middle man", "warning", type,
                    $"{delegationCount}/{methodCount} methods delegate", solutionDir));
        }

        return results;
    }

    // ──────────────────────────────────────────────
    // Complexity: symbol-level checks
    // ──────────────────────────────────────────────

    public static List<SmellResult> CheckLargeClasses(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;

            var memberCount = type.GetMembers()
                .Count(m => !m.IsImplicitlyDeclared && m is not INamedTypeSymbol);

            if (memberCount > 40)
                results.Add(MakeResult("Large class", "critical", type,
                    $"{memberCount} members", solutionDir));
            else if (memberCount > 20)
                results.Add(MakeResult("Large class", "warning", type,
                    $"{memberCount} members", solutionDir));
        }
        return results;
    }

    public static List<SmellResult> CheckLongParameterLists(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsImplicitlyDeclared) continue;
                if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor))
                    continue;

                var count = method.Parameters.Length;
                if (count > 8)
                    results.Add(MakeResult("Long parameter list", "critical", method,
                        $"{count} params", solutionDir));
                else if (count > 5)
                    results.Add(MakeResult("Long parameter list", "warning", method,
                        $"{count} params", solutionDir));
            }
        }
        return results;
    }

    // ──────────────────────────────────────────────
    // Design: symbol-level checks
    // ──────────────────────────────────────────────

    public static List<SmellResult> CheckGodClasses(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;
            if (type.IsStatic) continue;

            var memberCount = type.GetMembers()
                .Count(m => !m.IsImplicitlyDeclared && m is not INamedTypeSymbol);
            if (memberCount <= 20) continue;

            var depTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var member in type.GetMembers())
            {
                ITypeSymbol? memberType = member switch
                {
                    IFieldSymbol f when !f.IsImplicitlyDeclared && !f.IsConst => f.Type,
                    IPropertySymbol p => p.Type,
                    _ => null
                };
                if (memberType != null
                    && memberType.SpecialType == SpecialType.None
                    && !SymbolEqualityComparer.Default.Equals(memberType, type))
                {
                    depTypes.Add(memberType);
                }
            }

            if (depTypes.Count > 5)
                results.Add(MakeResult("God class", "critical", type,
                    $"{memberCount} members, {depTypes.Count} dependencies", solutionDir));
        }
        return results;
    }

    public static List<SmellResult> CheckDataClasses(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;
            if (type.IsStatic || type.IsRecord) continue;

            var ordinaryMethods = type.GetMembers().OfType<IMethodSymbol>()
                .Count(m => !m.IsImplicitlyDeclared && m.MethodKind == MethodKind.Ordinary);
            var propertyCount = type.GetMembers().OfType<IPropertySymbol>()
                .Count(p => !p.IsImplicitlyDeclared);

            if (ordinaryMethods == 0 && propertyCount >= 2)
                results.Add(MakeResult("Data class", "info", type,
                    $"{propertyCount} properties, 0 methods", solutionDir));
        }
        return results;
    }

    public static List<SmellResult> CheckTooManyDependencies(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;
            if (type.IsStatic) continue;

            var maxCtorParams = type.Constructors
                .Where(c => !c.IsImplicitlyDeclared)
                .Select(c => c.Parameters.Length)
                .DefaultIfEmpty(0)
                .Max();

            if (maxCtorParams > 8)
                results.Add(MakeResult("Too many dependencies", "critical", type,
                    $"{maxCtorParams} constructor params", solutionDir));
            else if (maxCtorParams > 5)
                results.Add(MakeResult("Too many dependencies", "warning", type,
                    $"{maxCtorParams} constructor params", solutionDir));
        }
        return results;
    }

    // ──────────────────────────────────────────────
    // Inheritance: symbol-level checks
    // ──────────────────────────────────────────────

    public static List<SmellResult> CheckDeepInheritance(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;

            int depth = 0;
            var current = type.BaseType;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                depth++;
                current = current.BaseType;
            }

            if (depth > 3)
                results.Add(MakeResult("Deep inheritance", "warning", type,
                    $"{depth} levels deep", solutionDir));
        }
        return results;
    }

    public static List<SmellResult> CheckRefusedBequest(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;
            if (type.BaseType == null || type.BaseType.SpecialType == SpecialType.System_Object)
                continue;

            var overridableCount = type.BaseType.GetMembers()
                .Count(m => (m.IsVirtual || m.IsAbstract) && !m.IsImplicitlyDeclared);
            if (overridableCount < 3) continue;

            var overrideCount = type.GetMembers()
                .Count(m => m.IsOverride && !m.IsImplicitlyDeclared);

            var rate = (double)overrideCount / overridableCount;
            if (rate < 0.2)
                results.Add(MakeResult("Refused bequest", "info", type,
                    $"overrides {overrideCount}/{overridableCount} base members ({rate:P0})",
                    solutionDir));
        }
        return results;
    }

    public static List<SmellResult> CheckSpeculativeGenerality(
        Compilation compilation, string solutionDir)
    {
        var results = new List<SmellResult>();
        foreach (var type in SymbolResolver.GetAllNamedTypes(compilation))
        {
            if (!ShouldAnalyzeType(type)) continue;

            // Type-level type parameters
            foreach (var tp in type.TypeParameters)
            {
                if (!IsTypeParameterUsed(tp, type))
                    results.Add(MakeResult("Speculative generality", "info", type,
                        $"unused type parameter '{tp.Name}'", solutionDir));
            }

            // Method-level type parameters
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.IsImplicitlyDeclared) continue;
                foreach (var tp in method.TypeParameters)
                {
                    if (!IsMethodTypeParameterUsed(tp, method))
                        results.Add(MakeResult("Speculative generality", "info", method,
                            $"unused type parameter '{tp.Name}'", solutionDir));
                }
            }
        }
        return results;
    }

    private static bool IsTypeParameterUsed(ITypeParameterSymbol tp, INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IMethodSymbol m:
                    if (ReferencesTypeParam(m.ReturnType, tp)) return true;
                    if (m.Parameters.Any(p => ReferencesTypeParam(p.Type, tp))) return true;
                    break;
                case IPropertySymbol p:
                    if (ReferencesTypeParam(p.Type, tp)) return true;
                    break;
                case IFieldSymbol f:
                    if (ReferencesTypeParam(f.Type, tp)) return true;
                    break;
            }
        }
        return false;
    }

    private static bool IsMethodTypeParameterUsed(ITypeParameterSymbol tp, IMethodSymbol method)
    {
        if (ReferencesTypeParam(method.ReturnType, tp)) return true;
        return method.Parameters.Any(p => ReferencesTypeParam(p.Type, tp));
    }

    private static bool ReferencesTypeParam(ITypeSymbol type, ITypeParameterSymbol tp)
    {
        if (SymbolEqualityComparer.Default.Equals(type, tp)) return true;
        if (type is INamedTypeSymbol named)
            return named.TypeArguments.Any(arg => ReferencesTypeParam(arg, tp));
        if (type is IArrayTypeSymbol array)
            return ReferencesTypeParam(array.ElementType, tp);
        return false;
    }

    // ──────────────────────────────────────────────
    // Deep analysis: feature envy
    // ──────────────────────────────────────────────

    public static async Task<List<SmellResult>> CheckFeatureEnvyAsync(
        Compilation compilation, Solution solution, Project project, string solutionDir)
    {
        var results = new List<SmellResult>();

        foreach (var document in project.Documents)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();
            if (semanticModel == null || root == null) continue;

            foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol == null || methodSymbol.IsImplicitlyDeclared) continue;
                if (AnalysisService.HasExcludedAttribute(methodSymbol)) continue;

                var containingType = methodSymbol.ContainingType;
                if (containingType == null) continue;

                var body = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
                if (body == null) continue;

                var accessCounts = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
                int ownAccesses = 0;

                foreach (var access in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    var symbol = semanticModel.GetSymbolInfo(access).Symbol;
                    if (symbol?.ContainingType is not INamedTypeSymbol accessedType) continue;

                    if (SymbolEqualityComparer.Default.Equals(accessedType, containingType))
                        ownAccesses++;
                    else
                    {
                        accessCounts.TryGetValue(accessedType, out var count);
                        accessCounts[accessedType] = count + 1;
                    }
                }

                if (accessCounts.Count > 0)
                {
                    var max = accessCounts.MaxBy(kvp => kvp.Value);
                    if (max.Value > ownAccesses && max.Value >= 3)
                    {
                        results.Add(MakeResult("Feature envy", "warning", methodSymbol,
                            $"uses {max.Key.Name} ({max.Value} accesses vs {ownAccesses} own)",
                            solutionDir));
                    }
                }
            }
        }

        return results;
    }
}
