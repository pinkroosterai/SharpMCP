using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace SharpMCP.Services;

public sealed class WorkspaceManager : IDisposable
{
    private readonly Dictionary<string, CachedWorkspace> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromSeconds(5);

    public async Task<Solution> GetSolutionAsync(string solutionPath)
    {
        var normalized = Path.GetFullPath(solutionPath);

        if (!File.Exists(normalized))
            throw new FileNotFoundException($"Solution not found: {normalized}");

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(normalized, out var cached) && !IsStale(cached))
                return cached.Solution;

            // Dispose previous workspace if reloading
            if (cached != null)
                cached.Workspace.Dispose();

            var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                Console.Error.WriteLine($"Workspace warning: {e.Diagnostic.Message}");
            });

            Solution solution;
            if (normalized.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                solution = await workspace.OpenSolutionAsync(normalized);
            }
            else if (normalized.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(normalized);
                solution = project.Solution;
            }
            else
            {
                throw new ArgumentException(
                    $"Expected a .sln or .csproj file, got: {Path.GetFileName(normalized)}");
            }

            _cache[normalized] = new CachedWorkspace(workspace, solution, normalized, DateTime.UtcNow, DateTime.UtcNow);
            return solution;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Compilation> GetCompilationAsync(string solutionPath, string? projectName = null)
    {
        var solution = await GetSolutionAsync(solutionPath);
        var project = FindProject(solution, projectName);

        var compilation = await project.GetCompilationAsync()
            ?? throw new InvalidOperationException(
                $"Failed to get compilation for project '{project.Name}'.");

        return compilation;
    }

    public async Task<Project> GetProjectAsync(string solutionPath, string projectName)
    {
        var solution = await GetSolutionAsync(solutionPath);
        return FindProject(solution, projectName);
    }

    /// <summary>
    /// Applies a modified solution (e.g. from Renamer) to disk and updates the cache.
    /// </summary>
    public async Task ApplyChangesAsync(string solutionPath, Solution newSolution)
    {
        var normalized = Path.GetFullPath(solutionPath);

        await _lock.WaitAsync();
        try
        {
            if (!_cache.TryGetValue(normalized, out var cached))
                throw new InvalidOperationException("No workspace loaded for this solution path.");

            if (!cached.Workspace.TryApplyChanges(newSolution))
                throw new InvalidOperationException("Failed to apply changes to the workspace.");

            // Update cache with the new solution state
            _cache[normalized] = new CachedWorkspace(
                cached.Workspace, cached.Workspace.CurrentSolution,
                normalized, DateTime.UtcNow, DateTime.UtcNow);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invalidates the cache for a solution, forcing a reload on next access.
    /// </summary>
    public async Task InvalidateCacheAsync(string solutionPath)
    {
        var normalized = Path.GetFullPath(solutionPath);

        await _lock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(normalized, out var cached))
            {
                cached.Workspace.Dispose();
                _cache.Remove(normalized);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Applies changes, runs an optional post-apply action, then invalidates the cache — all under a single lock.
    /// </summary>
    public async Task ApplyChangesAndInvalidateAsync(string solutionPath, Solution newSolution, Action? postApplyAction = null)
    {
        var normalized = Path.GetFullPath(solutionPath);

        await _lock.WaitAsync();
        try
        {
            if (!_cache.TryGetValue(normalized, out var cached))
                throw new InvalidOperationException("No workspace loaded for this solution path.");

            if (!cached.Workspace.TryApplyChanges(newSolution))
                throw new InvalidOperationException("Failed to apply changes to the workspace.");

            postApplyAction?.Invoke();

            cached.Workspace.Dispose();
            _cache.Remove(normalized);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            foreach (var cached in _cache.Values)
                cached.Workspace.Dispose();
            _cache.Clear();
        }
        finally
        {
            _lock.Release();
        }
        _lock.Dispose();
    }

    private static Project FindProject(Solution solution, string? projectName)
    {
        if (projectName == null)
        {
            var first = solution.Projects.FirstOrDefault()
                ?? throw new InvalidOperationException("Solution contains no projects.");
            return first;
        }

        var project = solution.Projects.FirstOrDefault(
            p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));

        if (project == null)
        {
            var available = string.Join(", ", solution.Projects.Select(p => p.Name));
            throw new KeyNotFoundException(
                $"Project '{projectName}' not found. Available projects: {available}");
        }

        return project;
    }

    private static bool IsStale(CachedWorkspace cached)
    {
        // Time-gate: skip filesystem scan if checked recently
        if (DateTime.UtcNow - cached.LastStaleCheck < StaleCheckInterval)
            return false;

        // Update the check timestamp (mutable field on an otherwise immutable record)
        cached.LastStaleCheck = DateTime.UtcNow;

        try
        {
            var solutionDir = Path.GetDirectoryName(cached.NormalizedPath)!;
            var csFiles = Directory.EnumerateFiles(solutionDir, "*.cs", SearchOption.AllDirectories);
            var csprojFiles = Directory.EnumerateFiles(solutionDir, "*.csproj", SearchOption.AllDirectories);
            var slnFiles = Directory.EnumerateFiles(solutionDir, "*.sln", SearchOption.TopDirectoryOnly);
            var latestWrite = csFiles.Concat(csprojFiles).Concat(slnFiles)
                .Select(f => File.GetLastWriteTimeUtc(f))
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            return latestWrite > cached.LoadedAt;
        }
        catch
        {
            return true;
        }
    }

    private sealed class CachedWorkspace
    {
        public MSBuildWorkspace Workspace { get; }
        public Solution Solution { get; }
        public string NormalizedPath { get; }
        public DateTime LoadedAt { get; }
        public DateTime LastStaleCheck { get; set; }

        public CachedWorkspace(MSBuildWorkspace workspace, Solution solution, string normalizedPath, DateTime loadedAt, DateTime lastStaleCheck)
        {
            Workspace = workspace;
            Solution = solution;
            NormalizedPath = normalizedPath;
            LoadedAt = loadedAt;
            LastStaleCheck = lastStaleCheck;
        }
    }
}
