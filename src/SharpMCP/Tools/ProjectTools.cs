using System.ComponentModel;
using ModelContextProtocol.Server;
using SharpMCP.Formatting;
using SharpMCP.Models;
using SharpMCP.Services;

namespace SharpMCP.Tools;

[McpServerToolType]
public sealed class ProjectTools
{
    private readonly ProjectService _projectService;
    private readonly SymbolFormatter _formatter;

    public ProjectTools(ProjectService projectService, SymbolFormatter formatter)
    {
        _projectService = projectService;
        _formatter = formatter;
    }

    [McpServerTool(Name = "list_projects"), Description("List all projects in a solution with metadata (name, framework, output type, file count, project references).")]
    public async Task<string> ListProjects(
        [Description("Path to .sln or .csproj file")] string solutionPath)
    {
        try
        {
            var projects = await _projectService.ListProjectsAsync(solutionPath);
            if (projects.Count == 0)
                return "No projects found in solution.";

            var solutionName = Path.GetFileName(solutionPath);
            return _formatter.FormatProjectList(projects, solutionName);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_project_info"), Description("Get detailed metadata for a specific project (name, framework, output type, file count, references).")]
    public async Task<string> GetProjectInfo(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Project name within the solution")] string projectName)
    {
        try
        {
            var info = await _projectService.GetProjectInfoAsync(solutionPath, projectName);
            return _formatter.FormatProjectList([info], Path.GetFileName(solutionPath));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_project_references"), Description("List project-to-project references (dependency graph) for a project.")]
    public async Task<string> ListProjectReferences(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Project name within the solution")] string projectName)
    {
        try
        {
            var refs = await _projectService.ListProjectReferencesAsync(solutionPath, projectName);
            if (refs.Count == 0)
                return $"{projectName} has no project references.";

            return $"{projectName} references:\n" + string.Join("\n", refs.Select(r => $"  - {r}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_package_references"), Description("List NuGet package references for a project (name + version).")]
    public async Task<string> ListPackageReferences(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Project name within the solution")] string projectName)
    {
        try
        {
            var packages = await _projectService.ListPackageReferencesAsync(solutionPath, projectName);
            if (packages.Count == 0)
                return $"{projectName} has no NuGet package references.";

            return $"Packages in {projectName}:\n" +
                string.Join("\n", packages.Select(p => $"  {p.Name} {p.Version}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_source_files"), Description("List all source (.cs) files in a project.")]
    public async Task<string> ListSourceFiles(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Project name within the solution")] string projectName)
    {
        try
        {
            var files = await _projectService.ListSourceFilesAsync(solutionPath, projectName);
            if (files.Count == 0)
                return $"{projectName} has no source files.";

            return $"Source files in {projectName} ({files.Count}):\n" +
                string.Join("\n", files.Select(f => $"  {f}"));
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_diagnostics"), Description("Get compilation errors and warnings for a project (or all projects if projectName is omitted).")]
    public async Task<string> GetDiagnostics(
        [Description("Path to .sln or .csproj file")] string solutionPath,
        [Description("Optional project name; omit for all projects")] string? projectName = null)
    {
        try
        {
            var diagnostics = await _projectService.GetDiagnosticsAsync(solutionPath, projectName);
            if (diagnostics.Count == 0)
                return "No errors or warnings.";

            var scope = projectName ?? Path.GetFileName(solutionPath);
            return $"Diagnostics for {scope} ({diagnostics.Count}):\n" +
                _formatter.FormatDiagnosticList(diagnostics);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
