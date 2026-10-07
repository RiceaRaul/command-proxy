using System.Security.Cryptography;
using System.Text.Json;

namespace Cpx.Rules;

/// <summary>
/// Project rule files are applied only when their SHA-256 is recorded here.
/// This is consent plus tamper evidence, not a sandbox.
/// </summary>
static class Trust
{
    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static bool IsTrusted(string path)
    {
        try
        {
            return Load().TryGetValue(Path.GetFullPath(path), out var hash) && hash == Hash(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void Add(IEnumerable<string> paths)
    {
        var store = Load();
        foreach (var path in paths) store[Path.GetFullPath(path)] = Hash(path);
        Save(store);
    }

    public static int Remove(IEnumerable<string> paths)
    {
        var store = Load();
        var removed = 0;
        foreach (var path in paths)
            if (store.Remove(Path.GetFullPath(path))) removed++;
        if (removed > 0) Save(store);
        return removed;
    }

    static Dictionary<string, string> Load()
    {
        if (!File.Exists(Paths.TrustFile)) return [];
        using var stream = File.OpenRead(Paths.TrustFile);
        return JsonSerializer.Deserialize(stream, CpxJson.Default.DictionaryStringString) ?? [];
    }

    static void Save(Dictionary<string, string> store)
    {
        Directory.CreateDirectory(Paths.DataDir);
        using var stream = File.Create(Paths.TrustFile);
        JsonSerializer.Serialize(stream, store, CpxJson.Default.DictionaryStringString);
    }
}
