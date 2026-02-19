using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpMCP.Formatting;

namespace SharpMCP.Services;

public sealed class SignatureService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SymbolResolver _symbolResolver;

    public SignatureService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
    {
        _workspaceManager = workspaceManager;
        _symbolResolver = symbolResolver;
    }

    public async Task<string> ChangeSignatureAsync(
        string solutionPath, string methodName, string? typeName = null,
        string? addParameters = null, string? removeParameters = null,
        string? reorderParameters = null)
    {
        var method = await _symbolResolver.ResolveMethodAsync(solutionPath, methodName, typeName);
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        var oldParams = method.Parameters.Select(p => p.Name).ToList();
        var oldSignature = FormatSignature(method);

        // Parse changes
        var paramsToAdd = ParseAddParameters(addParameters);
        var paramsToRemove = ParseRemoveParameters(removeParameters);
        var newOrder = ParseReorderParameters(reorderParameters);

        // Validate
        foreach (var name in paramsToRemove)
        {
            if (!oldParams.Contains(name))
                throw new ArgumentException($"Parameter '{name}' not found in method '{methodName}'. Existing parameters: {string.Join(", ", oldParams)}");
        }

        if (newOrder.Count > 0)
        {
            var surviving = oldParams.Except(paramsToRemove).ToHashSet();
            foreach (var name in newOrder)
            {
                if (!surviving.Contains(name))
                    throw new ArgumentException($"Reorder references unknown parameter '{name}'.");
            }
        }

        // Compute new parameter list
        var newParams = ComputeNewParameterList(method, paramsToAdd, paramsToRemove, newOrder);

        // Find all call sites
        var callers = await SymbolFinder.FindCallersAsync(method, solution);
        var callSiteLocations = callers
            .Where(c => c.IsDirect)
            .SelectMany(c => c.Locations)
            .Where(l => l.IsInSource)
            .ToList();

        // First, modify the method declaration
        var declLocation = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (declLocation?.SourceTree == null)
            throw new InvalidOperationException($"Cannot find source for method '{methodName}'.");

        // Find all documents we need to modify
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Modify declaration
        var declTree = declLocation.SourceTree;
        var declRoot = await declTree.GetRootAsync();
        var declNode = declRoot.FindNode(declLocation.SourceSpan);

        // Find the method declaration syntax
        var methodDecl = declNode.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()
            ?? declNode.AncestorsAndSelf().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() as SyntaxNode;

        if (methodDecl == null)
            throw new InvalidOperationException($"Cannot find method declaration syntax for '{methodName}'.");

        var declParamList = methodDecl switch
        {
            MethodDeclarationSyntax m => m.ParameterList,
            LocalFunctionStatementSyntax l => l.ParameterList,
            _ => throw new InvalidOperationException("Unsupported method syntax.")
        };

        // Build new parameter list for declaration
        var newDeclParamText = newParams;
        var declDocument = solution.GetDocumentIdsWithFilePath(declTree.FilePath).FirstOrDefault();

        if (declDocument != null && declTree.FilePath != null)
        {
            var sourceText = await declTree.GetTextAsync();
            var text = sourceText.ToString();

            // Replace the parameter list in the declaration
            var newText = text.Substring(0, declParamList.SpanStart)
                + $"({newDeclParamText})"
                + text.Substring(declParamList.Span.End);

            await File.WriteAllTextAsync(declTree.FilePath, newText);
            changedFiles.Add(declTree.FilePath);
        }

        // Modify call sites
        // Group locations by file, process each file once
        var locationsByFile = callSiteLocations.GroupBy(l => l.SourceTree?.FilePath).Where(g => g.Key != null);

        foreach (var fileGroup in locationsByFile)
        {
            var filePath = fileGroup.Key!;
            var tree = fileGroup.First().SourceTree!;
            var root = await tree.GetRootAsync();
            var text = (await tree.GetTextAsync()).ToString();

            // Collect all invocations in this file, sorted by position descending (to avoid offset shifts)
            var invocations = new List<(int Start, int End, string NewArgs)>();

            if (changedFiles.Contains(filePath))
            {
                // Same file as declaration — re-read and re-parse since spans have shifted
                text = await File.ReadAllTextAsync(filePath);
                var newTree = CSharpSyntaxTree.ParseText(text, path: filePath);
                root = await newTree.GetRootAsync();

                // Find invocations by method name in the re-parsed tree
                var matchingInvocations = root.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv =>
                    {
                        var name = inv.Expression switch
                        {
                            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                            IdentifierNameSyntax id => id.Identifier.ValueText,
                            _ => null
                        };
                        return name == method.Name;
                    });

                foreach (var invocation in matchingInvocations)
                {
                    var argList = invocation.ArgumentList;
                    var newArgText = BuildNewArgumentListText(
                        argList, method.Parameters, paramsToAdd, paramsToRemove, newOrder);
                    invocations.Add((argList.SpanStart, argList.Span.End, $"({newArgText})"));
                }
            }
            else
            {
                foreach (var location in fileGroup.OrderByDescending(l => l.SourceSpan.Start))
                {
                    var node = root.FindNode(location.SourceSpan);
                    var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
                    if (invocation == null) continue;

                    var argList = invocation.ArgumentList;
                    var newArgText = BuildNewArgumentListText(
                        argList, method.Parameters, paramsToAdd, paramsToRemove, newOrder);
                    invocations.Add((argList.SpanStart, argList.Span.End, $"({newArgText})"));
                }
            }

            // Apply changes from end to start to preserve positions
            foreach (var (start, end, newArgs) in invocations.OrderByDescending(i => i.Start))
            {
                text = text.Substring(0, start) + newArgs + text.Substring(end);
            }

            await File.WriteAllTextAsync(filePath, text);
            changedFiles.Add(filePath);
        }

        // Invalidate cache
        await _workspaceManager.InvalidateCacheAsync(solutionPath);

        // Build summary
        var newSignature = $"{methodName}({newDeclParamText})";
        var sb = new StringBuilder();
        var containingName = method.ContainingType?.Name;
        var displayName = containingName != null ? $"{containingName}.{methodName}" : methodName;

        sb.AppendLine($"Changed signature of '{displayName}':");
        sb.AppendLine($"  Before: {oldSignature}");
        sb.AppendLine($"  After:  {newSignature}");
        sb.AppendLine();

        if (paramsToRemove.Count > 0)
            sb.AppendLine($"  Removed: {string.Join(", ", paramsToRemove.Select(n => $"'{n}'"))}");
        if (newOrder.Count > 0)
            sb.AppendLine($"  Reordered parameters");
        if (paramsToAdd.Count > 0)
            sb.AppendLine($"  Added: {string.Join(", ", paramsToAdd.Select(p => $"'{p.Type} {p.Name}{(p.DefaultValue != null ? $" = {p.DefaultValue}" : "")}'"))}");

        sb.AppendLine();
        sb.AppendLine($"  Updated {changedFiles.Count} file(s):");
        foreach (var file in changedFiles.OrderBy(f => f))
            sb.AppendLine($"    {LocationFormatter.MakePathRelative(file, solutionDir)}");

        return sb.ToString().TrimEnd();
    }

    private record ParameterInfo(string Type, string Name, string? DefaultValue);

    private static List<ParameterInfo> ParseAddParameters(string? addParameters)
    {
        if (string.IsNullOrWhiteSpace(addParameters))
            return new List<ParameterInfo>();

        var result = new List<ParameterInfo>();
        foreach (var param in SplitParameters(addParameters))
        {
            var trimmed = param.Trim();
            string? defaultValue = null;
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex >= 0)
            {
                defaultValue = trimmed.Substring(eqIndex + 1).Trim();
                trimmed = trimmed.Substring(0, eqIndex).Trim();
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new ArgumentException($"Invalid parameter format: '{param}'. Expected 'type name' or 'type name=defaultValue'.");

            // Type might include generics or namespace qualifiers
            var type = string.Join(" ", parts.Take(parts.Length - 1));
            var name = parts.Last();
            result.Add(new ParameterInfo(type, name, defaultValue));
        }

        return result;
    }

    private static List<string> ParseRemoveParameters(string? removeParameters)
    {
        if (string.IsNullOrWhiteSpace(removeParameters))
            return new List<string>();

        return removeParameters.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToList();
    }

    private static List<string> ParseReorderParameters(string? reorderParameters)
    {
        if (string.IsNullOrWhiteSpace(reorderParameters))
            return new List<string>();

        return reorderParameters.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .ToList();
    }

    private static IEnumerable<string> SplitParameters(string input)
    {
        // Split on commas but respect angle brackets for generics
        var depth = 0;
        var start = 0;
        for (var i = 0; i < input.Length; i++)
        {
            switch (input[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    yield return input.Substring(start, i - start);
                    start = i + 1;
                    break;
            }
        }
        yield return input.Substring(start);
    }

    private static string ComputeNewParameterList(
        IMethodSymbol method,
        List<ParameterInfo> paramsToAdd,
        List<string> paramsToRemove,
        List<string> newOrder)
    {
        // Start with existing params minus removed ones
        var surviving = method.Parameters
            .Where(p => !paramsToRemove.Contains(p.Name))
            .ToList();

        // Reorder if specified
        if (newOrder.Count > 0)
        {
            var reordered = new List<IParameterSymbol>();
            foreach (var name in newOrder)
            {
                var param = surviving.FirstOrDefault(p => p.Name == name);
                if (param != null)
                    reordered.Add(param);
            }
            // Add any surviving params not mentioned in reorder (append at end)
            foreach (var param in surviving)
            {
                if (!newOrder.Contains(param.Name))
                    reordered.Add(param);
            }
            surviving = reordered;
        }

        // Build the text
        var parts = new List<string>();
        foreach (var param in surviving)
        {
            var prefix = param.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                _ => ""
            };
            var paramsKeyword = param.IsParams ? "params " : "";
            var defaultVal = param.HasExplicitDefaultValue
                ? $" = {FormatDefaultValue(param)}"
                : "";
            parts.Add($"{prefix}{paramsKeyword}{param.Type.ToDisplayString()} {param.Name}{defaultVal}");
        }

        // Add new parameters
        foreach (var p in paramsToAdd)
        {
            var defaultVal = p.DefaultValue != null ? $" = {p.DefaultValue}" : "";
            parts.Add($"{p.Type} {p.Name}{defaultVal}");
        }

        return string.Join(", ", parts);
    }

    private static string BuildNewArgumentListText(
        ArgumentListSyntax argList,
        ImmutableArray<IParameterSymbol> oldParams,
        List<ParameterInfo> paramsToAdd,
        List<string> paramsToRemove,
        List<string> newOrder)
    {
        var oldArgs = argList.Arguments.ToList();

        // Map arguments to parameter names
        var argsByName = new Dictionary<string, string>();
        for (int i = 0; i < oldArgs.Count && i < oldParams.Length; i++)
        {
            var arg = oldArgs[i];
            var paramName = arg.NameColon != null
                ? arg.NameColon.Name.Identifier.ValueText
                : oldParams[i].Name;
            argsByName[paramName] = arg.ToString();
        }

        // Remove parameters
        foreach (var name in paramsToRemove)
            argsByName.Remove(name);

        // Build argument list in the new order
        var survivingNames = oldParams
            .Select(p => p.Name)
            .Where(n => !paramsToRemove.Contains(n))
            .ToList();

        if (newOrder.Count > 0)
        {
            var reordered = new List<string>();
            foreach (var name in newOrder)
            {
                if (survivingNames.Contains(name))
                    reordered.Add(name);
            }
            foreach (var name in survivingNames)
            {
                if (!newOrder.Contains(name))
                    reordered.Add(name);
            }
            survivingNames = reordered;
        }

        var argTexts = new List<string>();
        foreach (var name in survivingNames)
        {
            if (argsByName.TryGetValue(name, out var argText))
                argTexts.Add(argText);
        }

        // Don't add arguments for new params with defaults (the default will apply)
        // For new params without defaults, add default(T)
        foreach (var p in paramsToAdd)
        {
            if (p.DefaultValue == null)
                argTexts.Add($"default({p.Type})");
        }

        return string.Join(", ", argTexts);
    }

    private static string FormatSignature(IMethodSymbol method)
    {
        var name = method.Name;
        var parameters = string.Join(", ", method.Parameters.Select(p =>
        {
            var prefix = p.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                _ => ""
            };
            var defaultVal = p.HasExplicitDefaultValue
                ? $" = {FormatDefaultValue(p)}"
                : "";
            return $"{prefix}{p.Type.ToDisplayString()} {p.Name}{defaultVal}";
        }));
        return $"{name}({parameters})";
    }

    private static string FormatDefaultValue(IParameterSymbol param)
    {
        if (!param.HasExplicitDefaultValue)
            return "";

        var value = param.ExplicitDefaultValue;
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? "default"
        };
    }
}
