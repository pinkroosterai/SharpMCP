using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using SharpMCP.Formatting;
using SharpMCP.Models;

namespace SharpMCP.Services;

public sealed class ProjectService
{
    private readonly WorkspaceManager _workspaceManager;

    public ProjectService(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<Models.ProjectInfo>> ListProjectsAsync(string solutionPath)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var results = new List<Models.ProjectInfo>();

        foreach (var project in solution.Projects)
        {
            results.Add(BuildProjectInfo(project, solution));
        }

        return results;
    }

    public async Task<Models.ProjectInfo> GetProjectInfoAsync(string solutionPath, string projectName)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var project = await _workspaceManager.GetProjectAsync(solutionPath, projectName);
        return BuildProjectInfo(project, solution);
    }

    private static Models.ProjectInfo BuildProjectInfo(Project project, Solution solution)
    {
        var doc = TryLoadProjectXml(project);
        var projectRefs = project.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId)?.Name ?? "unknown")
            .ToList();
        var packageRefs = ParsePackageReferences(doc);

        return new Models.ProjectInfo(
            Name: project.Name,
            FilePath: project.FilePath ?? "(no path)",
            TargetFramework: ParseTargetFramework(doc),
            OutputType: ParseOutputType(doc),
            SourceFileCount: project.Documents.Count(),
            ProjectReferences: projectRefs,
            PackageReferences: packageRefs
        );
    }

    public async Task<List<string>> ListSourceFilesAsync(string solutionPath, string projectName)
    {
        var project = await _workspaceManager.GetProjectAsync(solutionPath, projectName);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;

        return project.Documents
            .Where(d => d.FilePath != null)
            .Select(d => LocationFormatter.MakePathRelative(d.FilePath!, solutionDir))
            .OrderBy(p => p)
            .ToList();
    }

    public async Task<List<DiagnosticInfo>> GetDiagnosticsAsync(string solutionPath, string? projectName)
    {
        var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var projects = projectName != null
            ? [await _workspaceManager.GetProjectAsync(solutionPath, projectName)]
            : solution.Projects.ToList();

        var results = new List<DiagnosticInfo>();

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var diag in compilation.GetDiagnostics()
                .Where(d => d.Severity >= DiagnosticSeverity.Warning))
            {
                var location = diag.Location;
                var filePath = location.SourceTree?.FilePath ?? "(generated)";
                var line = location.GetLineSpan().StartLinePosition.Line + 1;

                results.Add(new DiagnosticInfo(
                    Id: diag.Id,
                    Severity: diag.Severity.ToString(),
                    Message: diag.GetMessage(),
                    FilePath: LocationFormatter.MakePathRelative(filePath, solutionDir),
                    Line: line
                ));
            }
        }

        return results
            .OrderBy(d => d.Severity == "Error" ? 0 : 1)
            .ThenBy(d => d.FilePath)
            .ThenBy(d => d.Line)
            .ToList();
    }

    private static List<PackageInfo> ParsePackageReferences(XDocument? doc)
    {
        if (doc == null) return [];
        try
        {
            return doc.Descendants("PackageReference")
                .Select(pr => new PackageInfo(
                    Name: pr.Attribute("Include")?.Value ?? "unknown",
                    Version: pr.Attribute("Version")?.Value ?? pr.Element("Version")?.Value ?? "unknown"
                ))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static XDocument? TryLoadProjectXml(Project project)
    {
        if (project.FilePath == null) return null;
        try { return XDocument.Load(project.FilePath); }
        catch { return null; }
    }

    private static string ParseTargetFramework(XDocument? doc)
    {
        if (doc == null) return "unknown";

        return doc.Descendants("TargetFramework").FirstOrDefault()?.Value
            ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value
            ?? "unknown";
    }

    private static string ParseOutputType(XDocument? doc)
    {
        if (doc == null) return "Library";

        return doc.Descendants("OutputType").FirstOrDefault()?.Value ?? "Library";
    }
}
