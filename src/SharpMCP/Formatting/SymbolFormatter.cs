using Microsoft.CodeAnalysis;
using SharpMCP.Models;

namespace SharpMCP.Formatting;

public sealed class SymbolFormatter
{
    private readonly LocationFormatter _locationFormatter;

    public SymbolFormatter(LocationFormatter locationFormatter)
    {
        _locationFormatter = locationFormatter;
    }

    public string FormatSymbol(ISymbol symbol, DetailLevel detail, string? solutionDir = null)
    {
        var location = symbol.Locations.FirstOrDefault();
        var locationStr = location != null
            ? _locationFormatter.FormatLocation(location, solutionDir)
            : "(no location)";

        var signature = GetSignature(symbol);

        if (detail == DetailLevel.Full)
        {
            var parts = new List<string> { $"{signature}  [{locationStr}]" };

            var docComment = symbol.GetDocumentationCommentXml();
            if (!string.IsNullOrWhiteSpace(docComment))
                parts.Add($"  Doc: {ExtractSummary(docComment)}");

            var attrs = symbol.GetAttributes();
            if (attrs.Length > 0)
            {
                var attrStr = string.Join(", ", attrs.Select(a => $"[{a.AttributeClass?.Name}]"));
                parts.Add($"  Attributes: {attrStr}");
            }

            return string.Join("\n", parts);
        }

        return $"{signature}  [{locationStr}]";
    }

    public string FormatSymbolList(IEnumerable<SymbolResult> symbols, DetailLevel detail)
    {
        var lines = new List<string>();
        foreach (var s in symbols)
        {
            if (detail == DetailLevel.Full)
            {
                lines.Add($"  {s.Signature}  [{s.FilePath}:{s.Line}]");
                if (s.DocComment != null)
                    lines.Add($"    Doc: {s.DocComment}");
                if (s.SourceBody != null)
                    lines.Add($"    Source:\n{Indent(s.SourceBody, 6)}");
            }
            else
            {
                lines.Add($"  {s.Signature}  [{s.FilePath}:{s.Line}]");
            }
        }
        return string.Join("\n", lines);
    }

    public string FormatReferenceList(IEnumerable<ReferenceResult> references, DetailLevel detail)
    {
        var lines = new List<string>();
        foreach (var r in references)
        {
            var containingPart = r.ContainingSymbol != null ? $" in {r.ContainingSymbol}" : "";

            if (detail == DetailLevel.Full && r.ContextBefore != null)
                lines.Add($"    {r.ContextBefore}");

            lines.Add($"  {r.FilePath}:{r.Line}{containingPart} - {r.CodeSnippet}");

            if (detail == DetailLevel.Full && r.ContextAfter != null)
                lines.Add($"    {r.ContextAfter}");
        }
        return string.Join("\n", lines);
    }

    public string FormatTypeHierarchy(TypeHierarchyResult hierarchy)
    {
        var lines = new List<string>();
        lines.Add(hierarchy.TypeName);

        if (hierarchy.BaseTypes.Count > 0)
            lines.Add($"  bases: {string.Join(" -> ", hierarchy.BaseTypes)}");

        if (hierarchy.Interfaces.Count > 0)
            lines.Add($"  implements: {string.Join(", ", hierarchy.Interfaces)}");

        return string.Join("\n", lines);
    }

    public string FormatProjectList(IEnumerable<Models.ProjectInfo> projects, string solutionName)
    {
        var projectList = projects.ToList();
        var lines = new List<string>
        {
            $"Solution: {solutionName} ({projectList.Count} projects)"
        };

        foreach (var p in projectList)
        {
            var refs = p.ProjectReferences.Count > 0
                ? $" -> {string.Join(", ", p.ProjectReferences)}"
                : "";
            lines.Add($"  {p.Name,-30} {p.TargetFramework,-10} {p.OutputType,-10} {p.SourceFileCount} files{refs}");
        }

        return string.Join("\n", lines);
    }

    public string FormatDiagnosticList(IEnumerable<DiagnosticInfo> diagnostics)
    {
        var lines = new List<string>();
        foreach (var d in diagnostics)
        {
            lines.Add($"  {d.Severity} {d.Id}: {d.Message}  [{d.FilePath}:{d.Line}]");
        }
        return lines.Count > 0 ? string.Join("\n", lines) : "  No diagnostics.";
    }

    // --- Shared static helpers used by services to build SymbolResult records ---

    public static string GetSignature(ISymbol symbol)
    {
        var accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();

        return symbol switch
        {
            INamedTypeSymbol type => FormatTypeSignature(type),
            IMethodSymbol method => FormatMethodSignature(method),
            IPropertySymbol prop => $"{accessibility} {prop.Type.ToDisplayString()} {prop.Name} {{ {GetAccessors(prop)} }}",
            IFieldSymbol field => $"{accessibility} {field.Type.ToDisplayString()} {field.Name}",
            IEventSymbol evt => $"{accessibility} event {evt.Type.ToDisplayString()} {evt.Name}",
            _ => $"{accessibility} {symbol.Kind.ToString().ToLowerInvariant()} {symbol.Name}"
        };
    }

    public static string GetKindString(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            INamespaceSymbol => "namespace",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
    }

    public static SymbolResult BuildSymbolResult(ISymbol symbol, string solutionDir)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var filePath = location?.SourceTree?.FilePath;
        var line = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;

        return new SymbolResult(
            Name: symbol.Name,
            FullyQualifiedName: symbol.ToDisplayString(),
            Kind: GetKindString(symbol),
            Signature: GetSignature(symbol),
            FilePath: filePath != null ? LocationFormatter.MakePathRelative(filePath, solutionDir) : "(no source)",
            Line: line
        );
    }

    public static string? ExtractSummary(string? xmlDoc)
    {
        if (string.IsNullOrWhiteSpace(xmlDoc)) return null;

        var start = xmlDoc.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xmlDoc.IndexOf("</summary>", StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            return xmlDoc[(start + 9)..end].Trim()
                .Replace("\n", " ")
                .Replace("\r", "");
        }
        return null;
    }

    public static async Task<string?> GetSourceBodyAsync(ISymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return null;

        var node = await syntaxRef.GetSyntaxAsync();
        return node.ToFullString().Trim();
    }

    private static string FormatTypeSignature(INamedTypeSymbol type)
    {
        var accessibility = type.DeclaredAccessibility.ToString().ToLowerInvariant();
        var modifiers = new List<string>();
        if (type.IsAbstract && type.TypeKind == TypeKind.Class) modifiers.Add("abstract");
        if (type.IsSealed && type.TypeKind == TypeKind.Class) modifiers.Add("sealed");
        if (type.IsStatic) modifiers.Add("static");

        var kind = type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            TypeKind.Class => type.IsRecord ? "record" : "class",
            _ => type.TypeKind.ToString().ToLowerInvariant()
        };

        var bases = new List<string>();
        if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
            bases.Add(type.BaseType.Name);
        bases.AddRange(type.Interfaces.Select(i => i.Name));

        var basePart = bases.Count > 0 ? $" : {string.Join(", ", bases)}" : "";
        var modPart = modifiers.Count > 0 ? $"{string.Join(" ", modifiers)} " : "";

        return $"{accessibility} {modPart}{kind} {type.Name}{basePart}";
    }

    private static string FormatMethodSignature(IMethodSymbol method)
    {
        var accessibility = method.DeclaredAccessibility.ToString().ToLowerInvariant();
        var modifiers = new List<string>();
        if (method.IsStatic) modifiers.Add("static");
        if (method.IsAsync) modifiers.Add("async");
        if (method.IsVirtual) modifiers.Add("virtual");
        if (method.IsOverride) modifiers.Add("override");
        if (method.IsAbstract) modifiers.Add("abstract");

        var returnType = method.ReturnType.ToDisplayString();
        var parameters = string.Join(", ",
            method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));

        var modPart = modifiers.Count > 0 ? $"{string.Join(" ", modifiers)} " : "";
        return $"{accessibility} {modPart}{returnType} {method.Name}({parameters})";
    }

    private static string GetAccessors(IPropertySymbol prop)
    {
        var parts = new List<string>();
        if (prop.GetMethod != null) parts.Add("get;");
        if (prop.SetMethod != null) parts.Add("set;");
        return string.Join(" ", parts);
    }

    private static string Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join("\n", text.Split('\n').Select(line => prefix + line));
    }
}
