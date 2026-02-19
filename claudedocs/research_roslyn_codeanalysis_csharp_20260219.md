# Research Report: Using Microsoft.CodeAnalysis.CSharp (Roslyn) to Inspect, Create & Alter C# Projects

**Date:** 2026-02-19
**Depth:** Deep
**Confidence:** High

---

## Executive Summary

The **Roslyn** platform (Microsoft.CodeAnalysis) is the .NET Compiler Platform that exposes the C# and VB compilers as APIs. It provides a rich set of libraries for **parsing**, **analyzing**, **generating**, and **transforming** C# source code programmatically. Through its **Workspace APIs**, you can also load entire `.sln`/`.csproj` projects and apply modifications that persist back to disk. Roslyn is the foundation for IDE features, analyzers, code fixes, source generators, and custom code manipulation tools.

---

## 1. Core Architecture

Roslyn's API is layered into distinct concerns:

```
┌─────────────────────────────────────────────────┐
│                  Workspace Layer                 │
│  (MSBuildWorkspace, AdhocWorkspace, Solution,    │
│   Project, Document)                             │
├─────────────────────────────────────────────────┤
│                  Compiler Layer                  │
│  ┌──────────────┐    ┌────────────────────────┐ │
│  │ Syntax APIs   │    │ Semantic APIs           │ │
│  │ SyntaxTree    │    │ Compilation             │ │
│  │ SyntaxNode    │    │ SemanticModel           │ │
│  │ SyntaxToken   │    │ ISymbol                 │ │
│  │ SyntaxTrivia  │    │ ITypeSymbol             │ │
│  │ SyntaxFactory │    │ IMethodSymbol           │ │
│  └──────────────┘    └────────────────────────┘ │
├─────────────────────────────────────────────────┤
│                  Services Layer                  │
│  (Formatting, Simplification, Renaming,          │
│   DocumentEditor, SyntaxGenerator)               │
└─────────────────────────────────────────────────┘
```

### Key Principle: Immutability

All Roslyn syntax trees are **immutable**. Every modification produces a **new tree**. This makes them thread-safe and enables safe concurrent analysis, but requires a specific approach when making changes (using `With*()`, `ReplaceNode()`, `CSharpSyntaxRewriter`, or `DocumentEditor`).

---

## 2. NuGet Packages

### Core Packages

| Package | Purpose | Latest Stable |
|---------|---------|---------------|
| `Microsoft.CodeAnalysis.CSharp` | C# syntax parsing, compilation, semantic analysis | 5.0.0 |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | C# workspace support (required for MSBuild workspace) | 5.0.0 |
| `Microsoft.CodeAnalysis.Workspaces.Common` | Common workspace abstractions | 5.0.0 |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | Load .sln/.csproj files via MSBuild | 5.0.0 |
| `Microsoft.Build.Locator` | Locate MSBuild installation on the system | 1.7.8 |

### Installation for a Typical Project

```bash
dotnet new console -n MyRoslynTool
cd MyRoslynTool

# Core analysis (parsing + compilation + semantic model)
dotnet add package Microsoft.CodeAnalysis.CSharp

# To load .sln/.csproj files
dotnet add package Microsoft.CodeAnalysis.CSharp.Workspaces
dotnet add package Microsoft.CodeAnalysis.Workspaces.MSBuild
dotnet add package Microsoft.Build.Locator
```

> **Important:** When using `Microsoft.CodeAnalysis.Workspaces.MSBuild`, you must call `MSBuildLocator.RegisterDefaults()` **before** any code that references MSBuild types. This locates the correct MSBuild SDK on the system.

---

## 3. Inspecting Code: Syntax Analysis

### Parsing Source Text into a SyntaxTree

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string code = @"
using System;

namespace MyApp
{
    public class Calculator
    {
        public int Add(int a, int b) => a + b;
    }
}";

SyntaxTree tree = CSharpSyntaxTree.ParseText(code);
CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
```

### Navigating the Syntax Tree

```csharp
// Usings
foreach (UsingDirectiveSyntax u in root.Usings)
    Console.WriteLine($"Using: {u.Name}");

// Namespaces -> Classes -> Methods
foreach (var ns in root.Members.OfType<NamespaceDeclarationSyntax>())
{
    foreach (var cls in ns.Members.OfType<ClassDeclarationSyntax>())
    {
        Console.WriteLine($"Class: {cls.Identifier}");
        foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>())
        {
            Console.WriteLine($"  Method: {method.Identifier}({string.Join(", ",
                method.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"))})");
        }
    }
}
```

### Using Descendant Queries

```csharp
// Find all method declarations in the tree
var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

// Find all string literals
var strings = root.DescendantTokens()
    .Where(t => t.IsKind(SyntaxKind.StringLiteralToken));

// Find all invocations of a specific method name
var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
    .Where(inv => inv.Expression.ToString() == "Console.WriteLine");
```

### Walking Trees with CSharpSyntaxWalker

For systematic tree traversal, subclass `CSharpSyntaxWalker`:

```csharp
public class MethodCollector : CSharpSyntaxWalker
{
    public List<string> MethodNames { get; } = new();

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        MethodNames.Add(node.Identifier.Text);
        base.VisitMethodDeclaration(node);  // Continue walking
    }
}

// Usage
var collector = new MethodCollector();
collector.Visit(root);
// collector.MethodNames now contains all method names
```

### Syntax Trivia

Trivia represents whitespace, comments, preprocessor directives -- everything that isn't semantic:

```csharp
// Find all comments in the file
var comments = root.DescendantTrivia()
    .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
             || t.IsKind(SyntaxKind.MultiLineCommentTrivia));

// Check for preprocessor directives
bool hasIfDirective = root.DescendantTrivia()
    .Any(t => t.IsKind(SyntaxKind.IfDirectiveTrivia));
```

---

## 4. Inspecting Code: Semantic Analysis

Semantic analysis goes beyond syntax to understand types, symbols, and meaning.

### Creating a Compilation

```csharp
// Create a compilation (combines syntax trees + references)
var compilation = CSharpCompilation.Create("MyCompilation",
    syntaxTrees: new[] { tree },
    references: new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        // Add more assembly references as needed
    });
```

### Getting the SemanticModel

```csharp
SemanticModel model = compilation.GetSemanticModel(tree);
```

### Querying Symbol Information

```csharp
// Bind a name to find its symbol
var usingSystem = root.Usings[0];
SymbolInfo nameInfo = model.GetSymbolInfo(usingSystem.Name!);
ISymbol? symbol = nameInfo.Symbol;
Console.WriteLine($"Symbol: {symbol?.Name}, Kind: {symbol?.Kind}");

// Get type info for an expression
var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
IMethodSymbol? methodSymbol = model.GetDeclaredSymbol(method);
Console.WriteLine($"Return type: {methodSymbol?.ReturnType}");
Console.WriteLine($"Parameters: {string.Join(", ",
    methodSymbol?.Parameters.Select(p => $"{p.Type} {p.Name}") ?? Array.Empty<string>())}");
```

### Finding Symbols by Name

```csharp
// Find all symbols with a given name
var symbols = compilation.GetSymbolsWithName("Add");
foreach (var s in symbols)
{
    Console.WriteLine($"{s.Kind}: {s.ToDisplayString()}");
}

// Check type relationships
if (methodSymbol?.ReturnType is INamedTypeSymbol returnType)
{
    Console.WriteLine($"Is value type: {returnType.IsValueType}");
    Console.WriteLine($"Is reference type: {returnType.IsReferenceType}");
    Console.WriteLine($"Base type: {returnType.BaseType}");
}
```

### Diagnostics (Errors & Warnings)

```csharp
// Get all compilation diagnostics
var diagnostics = compilation.GetDiagnostics();
foreach (var diag in diagnostics)
{
    Console.WriteLine($"{diag.Severity}: {diag.GetMessage()} at {diag.Location}");
}
```

---

## 5. Creating Code: SyntaxFactory

`SyntaxFactory` is the core API for creating syntax nodes from scratch.

### Recommended Import

```csharp
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
```

This eliminates the need to prefix every call with `SyntaxFactory.`.

### Creating a Complete Class

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

// Create: namespace CodeGenerationSample
var @namespace = NamespaceDeclaration(ParseName("CodeGenerationSample"))
    .NormalizeWhitespace();

// Add: using System;
@namespace = @namespace.AddUsings(
    UsingDirective(ParseName("System")));

// Create: public class Order : BaseEntity<Order>, IHaveIdentity
var classDeclaration = ClassDeclaration("Order")
    .AddModifiers(Token(SyntaxKind.PublicKeyword))
    .AddBaseListTypes(
        SimpleBaseType(ParseTypeName("BaseEntity<Order>")),
        SimpleBaseType(ParseTypeName("IHaveIdentity")));

// Create field: private bool canceled;
var fieldDeclaration = FieldDeclaration(
    VariableDeclaration(ParseTypeName("bool"))
        .AddVariables(VariableDeclarator("canceled")))
    .AddModifiers(Token(SyntaxKind.PrivateKeyword));

// Create property: public int Quantity { get; set; }
var propertyDeclaration = PropertyDeclaration(ParseTypeName("int"), "Quantity")
    .AddModifiers(Token(SyntaxKind.PublicKeyword))
    .AddAccessorListAccessors(
        AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
        AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));

// Create method: public void MarkAsCanceled() { canceled = true; }
var methodBody = ParseStatement("canceled = true;");
var methodDeclaration = MethodDeclaration(ParseTypeName("void"), "MarkAsCanceled")
    .AddModifiers(Token(SyntaxKind.PublicKeyword))
    .WithBody(Block(methodBody));

// Assemble the class
classDeclaration = classDeclaration.AddMembers(
    fieldDeclaration, propertyDeclaration, methodDeclaration);

// Add class to namespace
@namespace = @namespace.AddMembers(classDeclaration);

// Build compilation unit and output
var compilationUnit = CompilationUnit()
    .AddMembers(@namespace)
    .NormalizeWhitespace();

string code = compilationUnit.ToFullString();
Console.WriteLine(code);
```

**Output:**
```csharp
namespace CodeGenerationSample
{
    using System;

    public class Order : BaseEntity<Order>, IHaveIdentity
    {
        private bool canceled;

        public int Quantity { get; set; }

        public void MarkAsCanceled()
        {
            canceled = true;
        }
    }
}
```

### Shortcut: Parsing Fragments

For complex expressions or statements, use `Parse*` methods instead of building every node:

```csharp
// Parse a type name from string
TypeSyntax type = ParseTypeName("Dictionary<string, List<int>>");

// Parse a statement
StatementSyntax stmt = ParseStatement("var result = await service.GetDataAsync(id);");

// Parse an expression
ExpressionSyntax expr = ParseExpression("x > 0 ? x : -x");

// Parse an entire member
MemberDeclarationSyntax member = ParseMemberDeclaration(
    "public static string Hello(string name) => $\"Hello, {name}!\";")!;
```

### Formatting Generated Code

```csharp
using Microsoft.CodeAnalysis.Formatting;

// Option 1: NormalizeWhitespace (no workspace needed)
var formatted = compilationUnit.NormalizeWhitespace();

// Option 2: Formatter (requires workspace, more control)
var formattedNode = Formatter.Format(compilationUnit, new AdhocWorkspace());
```

---

## 6. Altering Code: Transformation Techniques

### Technique 1: ReplaceNode (Single Replacement)

```csharp
// Replace a specific node
var oldUsing = root.Usings[0];
var newUsing = oldUsing.WithName(ParseName("System.Collections.Generic"));
var newRoot = root.ReplaceNode(oldUsing, newUsing);
Console.WriteLine(newRoot.ToFullString());
```

### Technique 2: With* Methods (Copy-and-Modify)

Since syntax nodes are immutable, each `With*` call returns a new node:

```csharp
// Change a property from private to public
var property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().First();
var publicProperty = property
    .WithModifiers(new SyntaxTokenList(Token(SyntaxKind.PublicKeyword)));
var newRoot = root.ReplaceNode(property, publicProperty);
```

### Technique 3: CSharpSyntaxRewriter (Batch Transformations)

For systematic changes across an entire tree:

```csharp
public class TypeInferenceRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;

    public TypeInferenceRewriter(SemanticModel model) => _model = model;

    public override SyntaxNode? VisitLocalDeclarationStatement(
        LocalDeclarationStatementSyntax node)
    {
        // Only transform if exactly one variable is declared
        if (node.Declaration.Variables.Count != 1) return node;

        // Only transform if the variable has an initializer
        var declarator = node.Declaration.Variables.First();
        if (declarator.Initializer == null) return node;

        // Get the type from semantic model
        var variableTypeName = node.Declaration.Type;
        var variableType = _model.GetTypeInfo(variableTypeName).ConvertedType;

        if (variableType == null) return node;

        // Replace explicit type with 'var'
        var varTypeName = IdentifierName("var")
            .WithLeadingTrivia(variableTypeName.GetLeadingTrivia())
            .WithTrailingTrivia(variableTypeName.GetTrailingTrivia());

        return node.ReplaceNode(variableTypeName, varTypeName);
    }
}

// Usage
var rewriter = new TypeInferenceRewriter(semanticModel);
SyntaxNode newSource = rewriter.Visit(tree.GetRoot());
```

### Technique 4: DocumentEditor (Multiple Edits to One Document)

`DocumentEditor` solves the problem of applying multiple changes to the same document without node invalidation:

```csharp
using Microsoft.CodeAnalysis.Editing;

// Within a workspace context
var editor = await DocumentEditor.CreateAsync(document);

// Remove all empty statements
foreach (var emptyStatement in editor.OriginalRoot
    .DescendantNodes().OfType<EmptyStatementSyntax>())
{
    editor.RemoveNode(emptyStatement);
}

// Insert a node before another
var firstMethod = editor.OriginalRoot
    .DescendantNodes().OfType<MethodDeclarationSyntax>().First();
editor.InsertBefore(firstMethod, newFieldDeclaration);

// Replace a node
editor.ReplaceNode(oldNode, newNode);

// Get the changed document
var newDocument = editor.GetChangedDocument();
```

### Technique 5: SyntaxGenerator (Language-Agnostic)

`SyntaxGenerator` provides a higher-level, language-agnostic API:

```csharp
using Microsoft.CodeAnalysis.Editing;

var workspace = new AdhocWorkspace();
var generator = SyntaxGenerator.GetGenerator(workspace, LanguageNames.CSharp);

// Create a class with a method
var methodDecl = generator.MethodDeclaration(
    name: "GetName",
    returnType: generator.TypeExpression(SpecialType.System_String),
    accessibility: Accessibility.Public,
    statements: new[]
    {
        generator.ReturnStatement(
            generator.LiteralExpression("Hello"))
    });

var classDecl = generator.ClassDeclaration(
    name: "MyClass",
    accessibility: Accessibility.Public,
    members: new[] { methodDecl });

var namespaceDecl = generator.NamespaceDeclaration("MyNamespace", classDecl);
var compilationUnit = generator.CompilationUnit(namespaceDecl);

Console.WriteLine(compilationUnit.NormalizeWhitespace().ToFullString());
```

---

## 7. Working with Projects & Solutions: Workspace APIs

### Loading a Solution with MSBuildWorkspace

```csharp
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

// MUST be called before any MSBuild types are used
MSBuildLocator.RegisterDefaults();

using var workspace = MSBuildWorkspace.Create();

// Listen for workspace failures (important for debugging)
workspace.WorkspaceFailed += (sender, args) =>
    Console.WriteLine($"Workspace failure: {args.Diagnostic.Message}");

// Open a solution
var solution = await workspace.OpenSolutionAsync(@"/path/to/MySolution.sln");

// Or open a single project
// var project = await workspace.OpenProjectAsync(@"/path/to/MyProject.csproj");
```

### Enumerating Projects & Documents

```csharp
foreach (var project in solution.Projects)
{
    Console.WriteLine($"Project: {project.AssemblyName}");
    Console.WriteLine($"  Language: {project.Language}");
    Console.WriteLine($"  Documents: {project.Documents.Count()}");

    foreach (var document in project.Documents)
    {
        Console.WriteLine($"    {document.Name} ({document.FilePath})");

        // Get syntax tree
        var syntaxTree = await document.GetSyntaxTreeAsync();
        var root = await syntaxTree!.GetRootAsync();

        // Get semantic model
        var semanticModel = await document.GetSemanticModelAsync();

        // Get compilation (for the whole project)
        var compilation = await project.GetCompilationAsync();
    }
}
```

### Analyzing All Methods in a Solution

```csharp
foreach (var project in solution.Projects)
{
    var compilation = await project.GetCompilationAsync();
    if (compilation == null) continue;

    foreach (var syntaxTree in compilation.SyntaxTrees)
    {
        var model = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();

        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        foreach (var method in methods)
        {
            var symbol = model.GetDeclaredSymbol(method);
            Console.WriteLine($"{symbol?.ContainingType?.Name}.{symbol?.Name}" +
                $"({string.Join(", ", symbol?.Parameters.Select(p => p.Type) ?? Array.Empty<ITypeSymbol>())})");
        }
    }
}
```

### Modifying Documents & Applying Changes

```csharp
// Rewrite a document using CSharpSyntaxRewriter
foreach (var project in solution.Projects)
{
    foreach (var document in project.Documents)
    {
        var root = await document.GetSyntaxRootAsync();
        if (root == null) continue;

        var newRoot = new MyCustomRewriter().Visit(root);

        if (newRoot != root)
        {
            // Create new document with modified tree
            var newDocument = document.WithSyntaxRoot(newRoot);

            // Apply changes back to workspace (persists to disk for MSBuildWorkspace)
            if (!workspace.TryApplyChanges(newDocument.Project.Solution))
            {
                Console.WriteLine($"Failed to apply changes to {document.Name}");
            }
        }
    }
}
```

### Renaming Symbols Across a Solution

```csharp
using Microsoft.CodeAnalysis.Rename;

var compilation = await project.GetCompilationAsync();
var symbol = compilation!.GetSymbolsWithName("OldMethodName").Single();

var newSolution = await Renamer.RenameSymbolAsync(
    solution,
    symbol,
    new SymbolRenameOptions { RenameOverloads = true },
    "NewMethodName");

workspace.TryApplyChanges(newSolution);
```

### Using AdhocWorkspace (No Disk Projects)

For in-memory analysis or code generation without real project files:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

var workspace = new AdhocWorkspace();

var projectInfo = ProjectInfo.Create(
    ProjectId.CreateNewId(),
    VersionStamp.Create(),
    "TestProject",
    "TestProject",
    LanguageNames.CSharp)
    .WithMetadataReferences(new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
    });

var project = workspace.AddProject(projectInfo);

var document = workspace.AddDocument(project.Id,
    "TestFile.cs",
    SourceText.From(@"
        public class Foo
        {
            public void Bar() { }
        }"));

// Now you can analyze, modify, etc.
var syntaxRoot = await document.GetSyntaxRootAsync();
```

---

## 8. Editing .csproj Files Programmatically

For modifying project files themselves (not source code), use `Microsoft.Build.Evaluation`:

```csharp
using Microsoft.Build.Evaluation;

var collection = new ProjectCollection();
var project = collection.LoadProject("MyProject.csproj");

// Read properties
string targetFramework = project.GetPropertyValue("TargetFramework");
Console.WriteLine($"Target: {targetFramework}");

// Set properties
project.SetProperty("TreatWarningsAsErrors", "true");

// Add items (e.g., package references)
project.AddItem("PackageReference", "Newtonsoft.Json",
    new[] { new KeyValuePair<string, string>("Version", "13.0.3") });

// Save
project.Save();
```

> **Note:** This is the `Microsoft.Build` API, not Roslyn. It directly manipulates the MSBuild XML. For source code changes, use Roslyn's Workspace APIs.

---

## 9. Common Patterns & Recipes

### Find All Classes Implementing an Interface

```csharp
var compilation = await project.GetCompilationAsync();
var interfaceSymbol = compilation!.GetTypeByMetadataName("MyApp.IRepository");

var implementors = compilation.SyntaxTrees
    .SelectMany(tree =>
    {
        var model = compilation.GetSemanticModel(tree);
        return tree.GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Where(cls =>
            {
                var classSymbol = model.GetDeclaredSymbol(cls);
                return classSymbol?.AllInterfaces
                    .Any(i => SymbolEqualityComparer.Default.Equals(i, interfaceSymbol)) == true;
            });
    });
```

### Add a Using Directive to a File

```csharp
var root = await document.GetSyntaxRootAsync() as CompilationUnitSyntax;
if (root != null && !root.Usings.Any(u => u.Name?.ToString() == "System.Linq"))
{
    var newUsing = UsingDirective(ParseName("System.Linq"))
        .WithTrailingTrivia(ElasticCarriageReturnLineFeed);

    var newRoot = root.AddUsings(newUsing);
    var newDocument = document.WithSyntaxRoot(newRoot);
    workspace.TryApplyChanges(newDocument.Project.Solution);
}
```

### Add a Method to an Existing Class

```csharp
var root = await document.GetSyntaxRootAsync();
var classNode = root!.DescendantNodes().OfType<ClassDeclarationSyntax>()
    .First(c => c.Identifier.Text == "MyClass");

var newMethod = MethodDeclaration(ParseTypeName("string"), "GetId")
    .AddModifiers(Token(SyntaxKind.PublicKeyword))
    .WithBody(Block(ParseStatement("return Guid.NewGuid().ToString();")));

var newClass = classNode.AddMembers(newMethod);
var newRoot = root.ReplaceNode(classNode, newClass);
```

### Check Compilation Errors Before Emitting

```csharp
var compilation = CSharpCompilation.Create("Test",
    syntaxTrees: new[] { tree },
    references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

var diagnostics = compilation.GetDiagnostics()
    .Where(d => d.Severity == DiagnosticSeverity.Error);

if (diagnostics.Any())
{
    foreach (var error in diagnostics)
        Console.WriteLine($"ERROR: {error.GetMessage()}");
}
else
{
    // Emit to assembly
    using var ms = new MemoryStream();
    var result = compilation.Emit(ms);
    Console.WriteLine($"Emit success: {result.Success}");
}
```

---

## 10. Useful Tools

| Tool | Purpose |
|------|---------|
| **Syntax Visualizer** | VS extension (View > Other Windows > Syntax Visualizer) to explore syntax trees visually |
| **Roslyn Quoter** | [roslynquoter.azurewebsites.net](https://roslynquoter.azurewebsites.net/) -- paste C# code, get the SyntaxFactory calls to create it |
| **SharpLab** | [sharplab.io](https://sharplab.io/) -- explore syntax trees, IL, AST interactively |
| **LINQPad** | Supports Roslyn APIs for quick experimentation |

> **Roslyn Quoter** is particularly valuable -- paste any C# code and it generates the exact `SyntaxFactory` calls needed to produce that code programmatically.

---

## 11. Key Gotchas & Best Practices

### MSBuildLocator Must Be Called First

```csharp
// This MUST be the first thing before any MSBuild/Workspace code
MSBuildLocator.RegisterDefaults();
```

If you call any MSBuild-related types before this, you'll get assembly load failures.

### NuGet Restore Before Opening Solutions

MSBuildWorkspace needs resolved packages to correctly analyze projects:

```bash
dotnet restore MySolution.sln
```

### Listen for WorkspaceFailed Events

`MSBuildWorkspace` silently swallows many errors. Always attach a handler:

```csharp
workspace.WorkspaceFailed += (sender, args) =>
    Console.WriteLine($"WARNING: {args.Diagnostic.Message}");
```

### Immutability Awareness

Every modification creates a **new tree**. Nodes from the old tree cannot be used to navigate the new tree:

```csharp
// WRONG: oldNode no longer exists in newRoot
var newRoot = root.ReplaceNode(oldNode, newNode);
var found = newRoot.DescendantNodes().Contains(oldNode); // false!

// RIGHT: Track nodes or use DocumentEditor for multiple edits
```

### Trivia Preservation

When replacing nodes, trivia (whitespace, comments) can be lost. Use `WithLeadingTrivia()` / `WithTrailingTrivia()` to preserve formatting:

```csharp
var newNode = SyntaxFactory.ParseExpression("newValue")
    .WithLeadingTrivia(oldNode.GetLeadingTrivia())
    .WithTrailingTrivia(oldNode.GetTrailingTrivia());
```

### Formatting After Transformations

Always format generated/modified code:

```csharp
// Simple approach
var formatted = newRoot.NormalizeWhitespace();

// Full formatter (respects workspace options)
var formatted = Formatter.Format(newRoot, workspace);
```

### Performance with Large Solutions

- Use `GetCompilationAsync()` sparingly -- it compiles the entire project
- Prefer `GetSyntaxTreeAsync()` when you only need syntax analysis
- Use `project.GetDependencyGraph()` to process projects in dependency order

---

## 12. Package Dependency Summary

For a tool that needs to **load real projects, analyze code, and make modifications**:

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.0.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.0.0" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.0.0" />
</ItemGroup>
```

For a tool that **generates code from scratch** (no project loading needed):

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="5.0.0" />
</ItemGroup>
```

---

## Sources

- [Roslyn GitHub Repository](https://github.com/dotnet/roslyn)
- [Microsoft Learn: Get Started with Syntax Analysis](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-analysis)
- [Microsoft Learn: Get Started with Semantic Analysis](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/semantic-analysis)
- [Microsoft Learn: Get Started with Syntax Transformation](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/get-started/syntax-transformation)
- [Microsoft Learn: Tutorial - Write an Analyzer and Code Fix](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix)
- [MSDN Magazine: Language-Agnostic Code Generation with Roslyn](https://learn.microsoft.com/en-us/archive/msdn-magazine/2016/june/net-compiler-platform-language-agnostic-code-generation-with-roslyn)
- [Steve Gordon: Using Roslyn APIs to Analyse a .NET Solution](https://www.stevejgordon.co.uk/using-the-roslyn-apis-to-analyse-a-dotnet-solution)
- [Steve Gordon: Writing Code with Code (Roslyn)](https://www.stevejgordon.co.uk/getting-started-with-the-roslyn-apis-writing-code-with-code)
- [Meziantou: Using Roslyn to Analyze and Rewrite Code](https://www.meziantou.net/using-roslyn-to-analyze-and-rewrite-code-in-a-solution.htm)
- [Carlos Mendible: Create a Class with .NET Core and Roslyn](https://carlos.mendible.com/2017/03/02/create-a-class-with-net-core-and-roslyn/)
- [Strumenta: Getting Started with Roslyn - Transforming C# Code](https://tomassetti.me/getting-started-with-roslyn-transforming-c-code/)
- [PVS-Studio: Creating Roslyn API-based Static Analyzer](https://pvs-studio.com/en/blog/posts/csharp/0867/)
- [Jeremy Davis: Generating C# Code with Roslyn APIs](https://blog.jermdavis.dev/posts/2024/csharp-code-with-roslyn)
- [Brent Burnett: Using Roslyn to Power C# SDK Generation](https://btburnett.com/csharp/2022/12/09/using-roslyn-to-power-csharp-sdk-generation-from-openapi-specifications)
- [Josh Varty: Learn Roslyn Now - DocumentEditor](https://joshvarty.com/2015/08/18/learn-roslyn-now-part-12-the-documenteditor/)
- [Syncfusion: Walking through Roslyn Architecture](https://www.syncfusion.com/succinctly-free-ebooks/roslyn/walking-through-roslyn-architecture-apis-syntax)
- [NuGet: Microsoft.CodeAnalysis.CSharp.Workspaces](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Workspaces/)
- [NuGet: Microsoft.CodeAnalysis.Workspaces.MSBuild](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Workspaces.MSBuild/)
