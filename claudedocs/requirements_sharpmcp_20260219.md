# Requirements Specification: SharpMCP

**Date:** 2026-02-19
**Status:** Draft - Pending User Approval

---

## 1. Project Vision

**SharpMCP** is an MCP (Model Context Protocol) server that uses the Roslyn compiler platform to provide LLMs with efficient, surgical access to C# codebase intelligence. Instead of loading entire source files into context, LLMs can issue targeted queries -- "what implements this interface?", "where is this method called?", "what's the signature of this class?" -- and receive compact, structured answers.

### Problem Statement

LLMs working with C# codebases currently must read entire files (or large portions) to answer structural questions about the code. This wastes context tokens and is slow. A Roslyn-backed MCP server can provide precise answers with minimal token overhead.

### Target User

Developers using **Claude Code (CLI)** who work with C# solutions and want their LLM to understand their codebase structure without reading every file.

---

## 2. Functional Requirements

### FR-1: Solution/Project Loading

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-1.1 | Accept solution (.sln) or project (.csproj) path as a parameter on each tool call | Must |
| FR-1.2 | Internally cache loaded solutions/compilations to avoid re-loading on every query | Must |
| FR-1.3 | Detect when source files have changed and invalidate cache accordingly | Should |
| FR-1.4 | Support small-to-medium solutions (1-10 projects) with acceptable load times | Must |
| FR-1.5 | Report clear errors when a solution/project fails to load (missing SDK, NuGet restore needed, etc.) | Must |

### FR-2: Symbol Search & Overview

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-2.1 | List all types (classes, interfaces, enums, structs, records) in a project or solution | Must |
| FR-2.2 | Search for symbols by name (exact match and substring/pattern) | Must |
| FR-2.3 | Get a file-level symbol overview (list classes, methods, properties without source bodies) | Must |
| FR-2.4 | Get detailed symbol information: signature, modifiers, attributes, XML doc comments | Must |
| FR-2.5 | List all namespaces in a solution/project | Should |
| FR-2.6 | Filter symbol search by kind (class, interface, method, property, etc.) | Should |

### FR-3: Type Hierarchy & Implementations

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-3.1 | Find all classes that implement a given interface | Must |
| FR-3.2 | Find all classes that inherit from a given base class | Must |
| FR-3.3 | Get the full inheritance chain for a type (base classes + interfaces) | Must |
| FR-3.4 | Find all overrides of a virtual/abstract method | Should |
| FR-3.5 | List all members of a type (methods, properties, fields, events) with signatures | Must |

### FR-4: Find References / Call Sites

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-4.1 | Find all locations where a specific method is called | Must |
| FR-4.2 | Find all locations where a specific type is used (as parameter, return type, variable, etc.) | Must |
| FR-4.3 | Find all locations where a specific property/field is read or written | Should |
| FR-4.4 | Return results with file path, line number, and surrounding context snippet | Must |
| FR-4.5 | Support scoping reference search to a specific project or the entire solution | Should |

### FR-5: Project Structure & Dependencies

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-5.1 | List all projects in a solution with basic metadata (name, target framework, output type) | Must |
| FR-5.2 | List project-to-project references (dependency graph) | Must |
| FR-5.3 | List NuGet package references for a project (name + version) | Should |
| FR-5.4 | List source files in a project | Must |
| FR-5.5 | Get compilation diagnostics (errors and warnings) for a project | Should |

### FR-6: Source Code Retrieval

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-6.1 | Retrieve the source body of a specific symbol (method body, class declaration, etc.) | Must |
| FR-6.2 | Configurable verbosity: compact mode (signatures only) vs detailed mode (with source bodies) | Must |
| FR-6.3 | Retrieve a specific file's content (fallback for when targeted queries aren't sufficient) | Should |

---

## 3. Non-Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| NFR-1 | **Transport:** Stdio (for Claude Code CLI compatibility) | Must |
| NFR-2 | **Startup time:** Server should start and be ready for tool calls within 2 seconds (excluding initial solution load) | Should |
| NFR-3 | **Query response time:** Cached solution queries should respond within 1-3 seconds for small solutions | Should |
| NFR-4 | **Token efficiency:** Default output should be compact -- file paths, line numbers, signatures. No source bodies unless requested | Must |
| NFR-5 | **Error reporting:** All tools should return clear, actionable error messages | Must |
| NFR-6 | **Logging:** Log to stderr (not stdout, which is the MCP protocol channel) | Must |
| NFR-7 | **Target runtime:** .NET 9.0 | Must |
| NFR-8 | **Architecture:** Design to allow adding write/modification tools later without major refactoring | Should |

---

## 4. Proposed MCP Tool Inventory

### Group 1: Project Structure

| Tool Name | Description | Key Parameters |
|-----------|-------------|----------------|
| `list_projects` | List all projects in a solution | `solutionPath` |
| `get_project_info` | Get detailed project metadata | `projectPath` or `solutionPath` + `projectName` |
| `list_project_references` | List project-to-project dependencies | `projectPath` |
| `list_package_references` | List NuGet packages for a project | `projectPath` |
| `list_source_files` | List all .cs files in a project | `projectPath` |
| `get_diagnostics` | Get compilation errors/warnings | `projectPath` |

### Group 2: Symbol Search & Overview

| Tool Name | Description | Key Parameters |
|-----------|-------------|----------------|
| `find_symbol` | Search for symbols by name (supports patterns) | `solutionPath`, `query`, `kind?`, `detail?` |
| `get_file_symbols` | List all symbols in a specific file | `filePath`, `depth?`, `detail?` |
| `get_type_members` | List all members of a type | `solutionPath`, `typeName`, `detail?` |
| `get_symbol_info` | Get detailed info about a specific symbol | `solutionPath`, `symbolName`, `detail?` |
| `list_namespaces` | List all namespaces in a solution | `solutionPath` |

### Group 3: Type Hierarchy

| Tool Name | Description | Key Parameters |
|-----------|-------------|----------------|
| `find_implementations` | Find all classes implementing an interface | `solutionPath`, `interfaceName` |
| `find_subclasses` | Find all classes inheriting from a base class | `solutionPath`, `baseClassName` |
| `get_type_hierarchy` | Get full inheritance chain (bases + interfaces) | `solutionPath`, `typeName` |
| `find_overrides` | Find all overrides of a virtual/abstract method | `solutionPath`, `methodName`, `typeName` |

### Group 4: References & Call Sites

| Tool Name | Description | Key Parameters |
|-----------|-------------|----------------|
| `find_references` | Find all references to a symbol | `solutionPath`, `symbolName`, `scope?`, `detail?` |
| `find_callers` | Find all call sites for a method | `solutionPath`, `methodName`, `typeName?`, `detail?` |
| `find_usages` | Find where a type is used (params, fields, etc.) | `solutionPath`, `typeName`, `detail?` |

### Group 5: Source Retrieval

| Tool Name | Description | Key Parameters |
|-----------|-------------|----------------|
| `get_source` | Get source code for a symbol | `solutionPath`, `symbolName`, `typeName?` |
| `get_file_content` | Get a source file's content | `filePath`, `startLine?`, `endLine?` |

### Common Parameters

- **`detail`**: `"compact"` (default) or `"full"`. Compact = signatures + locations. Full = includes source bodies.
- **`solutionPath`** / **`projectPath`**: Path to .sln or .csproj. Server caches internally after first load.

---

## 5. User Stories

### US-1: Quick Codebase Orientation
> As an LLM assisting a developer, I want to quickly list all projects and key types in a solution so I can understand the codebase structure without reading every file.

**Acceptance Criteria:**
- `list_projects` returns project names, frameworks, and file counts
- `find_symbol` with no filter lists key types per project
- Results fit within a reasonable token budget (~500-1000 tokens for a small solution)

### US-2: Interface Implementation Discovery
> As an LLM, when a developer asks "what implements IOrderService?", I want to query the MCP server and get an exact list of implementing classes with their file locations.

**Acceptance Criteria:**
- `find_implementations("IOrderService")` returns class names, file paths, line numbers
- With `detail: "full"`, also returns the class signature and XML doc
- Works across project boundaries within the solution

### US-3: Method Call Site Analysis
> As an LLM, when a developer asks "where is ProcessPayment called?", I want to find all call sites with surrounding context.

**Acceptance Criteria:**
- `find_callers("ProcessPayment")` returns file, line, and a code snippet around each call
- Can disambiguate overloads if `typeName` is provided
- Results are ordered by file path for readability

### US-4: Type Exploration
> As an LLM, I want to understand a class's full API surface (methods, properties, events) without reading the entire file.

**Acceptance Criteria:**
- `get_type_members("OrderService")` returns all members with signatures
- Modifiers (public/private/static/async/virtual) are included
- With `detail: "compact"`, no method bodies are included

### US-5: Dependency Understanding
> As an LLM, I want to understand how projects depend on each other and what packages they use.

**Acceptance Criteria:**
- `list_project_references` shows the dependency graph
- `list_package_references` shows NuGet packages with versions
- Helps the LLM understand which project to look in for specific functionality

---

## 6. Architecture Considerations (for /sc:design phase)

These are NOT decisions yet -- just notes for the design phase:

- **Caching strategy:** Keep `MSBuildWorkspace` + `Compilation` in memory per solution path. Use a simple LRU or timeout-based eviction.
- **Concurrency:** MCP stdio servers handle one request at a time, so thread safety is less of a concern initially.
- **Symbol resolution:** Most queries require full `Compilation` + `SemanticModel`. Pre-compute on first load, then query synchronously.
- **Output formatting:** Build a shared formatter that converts Roslyn `ISymbol` / `SyntaxNode` data into compact text representations.
- **Extensibility:** Structure tools as separate classes (one per tool or per group) so adding write tools later is straightforward.

---

## 7. Open Questions

1. **Should the server support multiple solutions simultaneously?** (e.g., different `solutionPath` values in consecutive calls). Current assumption: yes, via caching.
2. **How to handle unrestorable solutions?** If `dotnet restore` hasn't been run, the semantic model will be incomplete. Should the server attempt a restore, or just report the issue?
3. **Should results include cross-assembly symbols?** (e.g., implementations from referenced NuGet packages, not just source projects). Current assumption: source-only for now.
4. **Name for the project?** "SharpMCP" is the repo name. Is this the desired product name?

---

## 8. Out of Scope (v1)

- Code modification / refactoring tools (deferred to v2)
- Source generators / analyzer integration
- Remote HTTP transport
- Multi-language support (VB.NET, F#)
- Real-time file watching / incremental updates
- Integration with git (blame, history)

---

## Next Steps

1. **Review this spec** -- Confirm, modify, or expand requirements
2. **`/sc:design`** -- Architecture and component design
3. **`/sc:implement`** -- Build it
