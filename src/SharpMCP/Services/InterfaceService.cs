using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpMCP.Formatting;

namespace SharpMCP.Services;

public sealed class InterfaceService
{
    private readonly WorkspaceManager _workspaceManager;
    private readonly SymbolResolver _symbolResolver;

    public InterfaceService(WorkspaceManager workspaceManager, SymbolResolver symbolResolver)
    {
        _workspaceManager = workspaceManager;
        _symbolResolver = symbolResolver;
    }

    public async Task<string> ExtractInterfaceAsync(
        string solutionPath, string typeName,
        string? interfaceName = null, bool apply = true)
    {
        var typeSymbol = await _symbolResolver.ResolveTypeAsync(solutionPath, typeName);

        if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            throw new ArgumentException($"'{typeName}' is a {typeSymbol.TypeKind}. extract_interface requires a class or struct.");

        interfaceName ??= $"I{typeSymbol.Name}";

        // Collect public non-static members (methods, properties, events)
        var members = typeSymbol.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                        && !m.IsStatic
                        && m is IMethodSymbol { MethodKind: MethodKind.Ordinary }
                            or IPropertySymbol
                            or IEventSymbol)
            .ToList();

        if (members.Count == 0)
            throw new InvalidOperationException($"'{typeName}' has no public non-static members to extract.");

        // Determine namespace
        var ns = typeSymbol.ContainingNamespace?.IsGlobalNamespace == false
            ? typeSymbol.ContainingNamespace.ToDisplayString()
            : null;

        // Generate interface source
        var interfaceCode = GenerateInterfaceCode(interfaceName, ns, members);

        var sb = new StringBuilder();
        sb.AppendLine($"Generated interface '{interfaceName}' with {members.Count} member(s) from '{typeName}'");
        sb.AppendLine();

        if (apply)
        {
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

            // Find the class source file to determine where to put the interface file
            var sourceLocation = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (sourceLocation?.SourceTree?.FilePath is not string classFilePath)
                throw new InvalidOperationException($"Cannot find source file for '{typeName}'.");

            var directory = Path.GetDirectoryName(classFilePath)!;
            var interfaceFilePath = Path.Combine(directory, $"{interfaceName}.cs");

            // Write the interface file
            await File.WriteAllTextAsync(interfaceFilePath, interfaceCode);

            // Modify the class declaration to add : IFoo
            await AddInterfaceToClassAsync(solutionPath, typeSymbol, interfaceName, classFilePath);

            sb.AppendLine($"Created: {LocationFormatter.MakePathRelative(interfaceFilePath, solutionDir)}");
            sb.AppendLine($"Modified: {LocationFormatter.MakePathRelative(classFilePath, solutionDir)} (added : {interfaceName})");
            sb.AppendLine();
        }

        sb.AppendLine("Interface:");
        sb.Append(interfaceCode);

        return sb.ToString().TrimEnd();
    }

    public async Task<string> ImplementInterfaceAsync(
        string solutionPath, string typeName, string? interfaceName = null)
    {
        var typeSymbol = await _symbolResolver.ResolveTypeAsync(solutionPath, typeName);

        if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            throw new ArgumentException($"'{typeName}' is a {typeSymbol.TypeKind}. implement_interface requires a class or struct.");

        // Find all interfaces the class declares
        var interfaces = typeSymbol.Interfaces;
        if (interfaceName != null)
        {
            interfaces = interfaces.Where(i =>
                string.Equals(i.Name, interfaceName, StringComparison.Ordinal)
                || string.Equals(i.ToDisplayString(), interfaceName, StringComparison.Ordinal))
                .ToImmutableArray();

            if (interfaces.Length == 0)
                throw new KeyNotFoundException(
                    $"'{typeName}' does not implement interface '{interfaceName}'.");
        }

        if (interfaces.Length == 0)
            throw new InvalidOperationException($"'{typeName}' does not implement any interfaces.");

        // Find unimplemented members across all target interfaces
        var existingMembers = typeSymbol.GetMembers()
            .Where(m => m is IMethodSymbol or IPropertySymbol or IEventSymbol)
            .ToHashSet(SymbolEqualityComparer.Default);

        var stubs = new List<(string InterfaceName, string MemberSignature, string StubCode)>();

        foreach (var iface in interfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (member is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
                    continue;

                // Check if the class already implements this member
                var impl = typeSymbol.FindImplementationForInterfaceMember(member);
                if (impl != null && existingMembers.Contains(impl))
                    continue;

                var stub = GenerateStub(member);
                if (stub != null)
                    stubs.Add((iface.Name, GetMemberSignatureDisplay(member), stub));
            }
        }

        if (stubs.Count == 0)
            return $"All interface members are already implemented in '{typeName}'.";

        // Insert stubs into the class
        var sourceLocation = typeSymbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (sourceLocation?.SourceTree is not SyntaxTree syntaxTree)
            throw new InvalidOperationException($"Cannot find source for '{typeName}'.");

        var root = await syntaxTree.GetRootAsync();
        var classDecl = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.ValueText == typeSymbol.Name);

        if (classDecl == null)
            throw new InvalidOperationException($"Cannot find class declaration for '{typeName}'.");

        // Build the combined stub block
        var stubBlock = new StringBuilder();
        stubBlock.AppendLine();
        foreach (var (ifaceName, _, code) in stubs)
        {
            stubBlock.AppendLine($"    {code}");
            stubBlock.AppendLine();
        }

        // Insert before the closing brace
        var closeBrace = classDecl.CloseBraceToken;

        // Use text-based insertion for reliability
        var sourceText = await syntaxTree.GetTextAsync();
        var insertPosition = closeBrace.SpanStart;
        var newText = sourceText.ToString().Insert(insertPosition, stubBlock.ToString());
        var filePath = syntaxTree.FilePath;
        await File.WriteAllTextAsync(filePath, newText);

        // Invalidate cache since we wrote to disk
        await _workspaceManager.InvalidateCacheAsync(solutionPath);

        // Build summary
        var result = new StringBuilder();
        var groupedByInterface = stubs.GroupBy(s => s.InterfaceName);
        foreach (var group in groupedByInterface)
        {
            result.AppendLine($"Implemented {group.Count()} member(s) from '{group.Key}' in '{typeName}':");
            foreach (var (_, sig, _) in group)
                result.AppendLine($"  + {sig}");
        }

        return result.ToString().TrimEnd();
    }

    private static string GenerateInterfaceCode(string interfaceName, string? ns, List<ISymbol> members)
    {
        var sb = new StringBuilder();

        if (ns != null)
            sb.AppendLine($"namespace {ns};");

        sb.AppendLine();
        sb.AppendLine($"public interface {interfaceName}");
        sb.AppendLine("{");

        foreach (var member in members)
        {
            switch (member)
            {
                case IMethodSymbol method:
                    sb.AppendLine($"    {FormatMethodSignature(method)};");
                    break;
                case IPropertySymbol property:
                    sb.AppendLine($"    {FormatPropertySignature(property)};");
                    break;
                case IEventSymbol evt:
                    sb.AppendLine($"    event {evt.Type.ToDisplayString()} {evt.Name};");
                    break;
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FormatMethodSignature(IMethodSymbol method)
    {
        var returnType = method.ReturnType.ToDisplayString();
        var name = method.Name;
        var typeParams = method.TypeParameters.Length > 0
            ? $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>"
            : "";
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        return $"{returnType} {name}{typeParams}({parameters})";
    }

    private static string FormatParameter(IParameterSymbol param)
    {
        var prefix = param.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => ""
        };
        var suffix = param.IsParams ? "params " : "";
        return $"{prefix}{suffix}{param.Type.ToDisplayString()} {param.Name}";
    }

    private static string FormatPropertySignature(IPropertySymbol property)
    {
        var accessors = new List<string>();
        if (property.GetMethod != null) accessors.Add("get");
        if (property.SetMethod != null)
            accessors.Add(property.SetMethod.IsInitOnly ? "init" : "set");
        return $"{property.Type.ToDisplayString()} {property.Name} {{ {string.Join("; ", accessors)}; }}";
    }

    private static string? GenerateStub(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => GenerateMethodStub(method),
            IPropertySymbol property => GeneratePropertyStub(property),
            IEventSymbol evt => $"public event {evt.Type.ToDisplayString()} {evt.Name};",
            _ => null
        };
    }

    private static string GenerateMethodStub(IMethodSymbol method)
    {
        var returnType = method.ReturnType.ToDisplayString();
        var name = method.Name;
        var typeParams = method.TypeParameters.Length > 0
            ? $"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>"
            : "";
        var parameters = string.Join(", ", method.Parameters.Select(FormatParameter));
        return $"public {returnType} {name}{typeParams}({parameters}) => throw new NotImplementedException();";
    }

    private static string GeneratePropertyStub(IPropertySymbol property)
    {
        var accessors = new List<string>();
        if (property.GetMethod != null)
            accessors.Add("get => throw new NotImplementedException();");
        if (property.SetMethod != null)
            accessors.Add(property.SetMethod.IsInitOnly
                ? "init => throw new NotImplementedException();"
                : "set => throw new NotImplementedException();");
        return $"public {property.Type.ToDisplayString()} {property.Name} {{ {string.Join(" ", accessors)} }}";
    }

    private static string GetMemberSignatureDisplay(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => FormatMethodSignature(method),
            IPropertySymbol property => FormatPropertySignature(property),
            IEventSymbol evt => $"event {evt.Type.ToDisplayString()} {evt.Name}",
            _ => member.ToDisplayString()
        };
    }

    private async Task AddInterfaceToClassAsync(
        string solutionPath, INamedTypeSymbol typeSymbol,
        string interfaceName, string classFilePath)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => string.Equals(d.FilePath, classFilePath, StringComparison.OrdinalIgnoreCase));

        if (document == null)
            throw new InvalidOperationException($"Cannot find document for '{classFilePath}' in the solution.");

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null) return;

        var root = await syntaxTree.GetRootAsync();
        var classDecl = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Identifier.ValueText == typeSymbol.Name);

        if (classDecl == null) return;

        // Add the interface to the base list
        var interfaceType = SyntaxFactory.SimpleBaseType(
            SyntaxFactory.ParseTypeName(interfaceName));

        TypeDeclarationSyntax newClassDecl;
        if (classDecl.BaseList == null)
        {
            var baseList = SyntaxFactory.BaseList(
                SyntaxFactory.SingletonSeparatedList<BaseTypeSyntax>(interfaceType))
                .WithLeadingTrivia(SyntaxFactory.Space);
            newClassDecl = classDecl.WithBaseList(baseList);
        }
        else
        {
            var newBaseList = classDecl.BaseList.AddTypes(interfaceType);
            newClassDecl = classDecl.WithBaseList(newBaseList);
        }

        var newRoot = root.ReplaceNode(classDecl, newClassDecl);
        var newText = newRoot.ToFullString();
        await File.WriteAllTextAsync(classFilePath, newText);

        // Invalidate cache since we wrote to disk
        await _workspaceManager.InvalidateCacheAsync(solutionPath);
    }
}
