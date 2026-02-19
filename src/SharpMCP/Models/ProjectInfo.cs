namespace SharpMCP.Models;

public record ProjectInfo(
    string Name,
    string FilePath,
    string TargetFramework,
    string OutputType,
    int SourceFileCount,
    List<string> ProjectReferences,
    List<PackageInfo> PackageReferences
);

public record PackageInfo(string Name, string Version);

public record DiagnosticInfo(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int Line
);
