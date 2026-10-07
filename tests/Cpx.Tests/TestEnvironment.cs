using System.Runtime.CompilerServices;

namespace Cpx.Tests;

/// <summary>Points cpx at throwaway config and data directories before any test runs.</summary>
static class TestEnvironment
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "cpx-tests-" + Guid.NewGuid().ToString("N"));
    public static string ConfigDir => Path.Combine(Root, "config");
    public static string DataDir => Path.Combine(Root, "data");

    [ModuleInitializer]
    public static void Initialize()
    {
        Directory.CreateDirectory(Path.Combine(ConfigDir, "rules"));
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("CPX_CONFIG_DIR", ConfigDir);
        Environment.SetEnvironmentVariable("CPX_DATA_DIR", DataDir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        };
    }

    public static string NewDirectory()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
