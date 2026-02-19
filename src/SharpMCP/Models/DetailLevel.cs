namespace SharpMCP.Models;

public enum DetailLevel
{
    Compact,
    Full
}

public static class DetailLevelExtensions
{
    public static DetailLevel Parse(string? value) =>
        value?.ToLowerInvariant() == "full" ? DetailLevel.Full : DetailLevel.Compact;
}
