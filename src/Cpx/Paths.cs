namespace Cpx;

/// <summary>Per-OS locations. CPX_CONFIG_DIR / CPX_DATA_DIR override them (used by tests).</summary>
static class Paths
{
    public const string ProjectDirName = ".cpx";

    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("CPX_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cpx");

    public static string DataDir =>
        Environment.GetEnvironmentVariable("CPX_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cpx");

    public static string UserRulesDir => Path.Combine(ConfigDir, "rules");
    public static string TrustFile => Path.Combine(DataDir, "trust.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.jsonl");
    public static string RawDir => Path.Combine(DataDir, "raw");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Nearest ancestor of <paramref name="cwd"/> (inclusive) that has a .cpx/rules directory.</summary>
    public static string? FindProjectRulesDir(string cwd)
    {
        for (var dir = cwd; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, ProjectDirName, "rules");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
