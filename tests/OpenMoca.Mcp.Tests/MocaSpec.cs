namespace OpenMoca.Mcp.Tests;

/// <summary>Finds the moca-spec checkout: <c>MOCA_SPEC_DIR</c>, else <c>../moca-spec</c> next to this repository.</summary>
internal static class MocaSpec
{
    public static string Root { get; } = Find();

    /// <summary>This repository's root: the folder holding moca-mcp-dotnet.slnx.</summary>
    public static string RepositoryRoot { get; } = FindRepository();

    private static string FindRepository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "moca-mcp-dotnet.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Cannot find the repository root (moca-mcp-dotnet.slnx).");
    }

    private static string Find()
    {
        var configured = Environment.GetEnvironmentVariable("MOCA_SPEC_DIR");
        if (!string.IsNullOrEmpty(configured))
        {
            return Path.GetFullPath(configured);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "moca-mcp-dotnet.slnx")))
            {
                return Path.GetFullPath(Path.Combine(dir.FullName, "..", "moca-spec"));
            }
        }

        throw new InvalidOperationException("Cannot find moca-spec. Clone it next to this repository or set MOCA_SPEC_DIR.");
    }
}
