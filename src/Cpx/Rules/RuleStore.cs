using System.Buffers;
using System.Reflection;
using System.Text.Json;

namespace Cpx.Rules;

enum RuleSource { Project, User, Embedded }

sealed record RuleMatch(Rule Rule, RuleSource Source, string Origin, int InsertAt)
{
    /// <summary>The arguments actually passed to the command: the original ones plus the rule's addArgs.</summary>
    public List<string> BuildArgs(string[] args)
    {
        var result = new List<string>(args.Length + (Rule.AddArgs?.Length ?? 0));
        result.AddRange(args.AsSpan(0, InsertAt));
        if (Rule.AddArgs is { } extra)
            foreach (var arg in extra)
                if (Array.IndexOf(args, arg) < 0) result.Add(arg);
        result.AddRange(args.AsSpan(InsertAt));
        return result;
    }
}

/// <summary>
/// Rule files are named after the command (git.json), so finding a rule is a direct lookup:
/// no directory scan and no parsing of rules that belong to other commands.
/// </summary>
static class RuleStore
{
    static readonly SearchValues<char> NameChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._+-");

    static readonly string[] WindowsExtensions = [".exe", ".cmd", ".bat", ".com"];

    /// <summary>"/usr/bin/git" and "git.exe" both become "git". Empty when the name cannot be a rule file name.</summary>
    public static string CommandName(string command)
    {
        var name = Path.GetFileName(command.AsSpan());
        if (OperatingSystem.IsWindows())
            foreach (var ext in WindowsExtensions)
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^ext.Length];
                    break;
                }

        if (name.IsEmpty || name is "." or ".." || name.ContainsAnyExcept(NameChars)) return "";
        return OperatingSystem.IsWindows() ? name.ToString().ToLowerInvariant() : name.ToString();
    }

    public static RuleMatch? Find(string command, string[] args, string cwd)
    {
        foreach (var match in Candidates(command, args, cwd)) return match;
        return null;
    }

    /// <summary>Matching rules in priority order: trusted project, user, embedded. Lazy: a later level is read only if asked for.</summary>
    public static IEnumerable<RuleMatch> Candidates(string command, string[] args, string cwd)
    {
        var name = CommandName(command);
        if (name.Length == 0) yield break;
        var fileName = name + ".json";

        if (Paths.FindProjectRulesDir(cwd) is { } projectDir)
        {
            var path = Path.Combine(projectDir, fileName);
            if (File.Exists(path) && Trust.IsTrusted(path) && MatchFile(path, RuleSource.Project, args) is { } match)
                yield return match;
        }

        var userPath = Path.Combine(Paths.UserRulesDir, fileName);
        if (File.Exists(userPath) && MatchFile(userPath, RuleSource.User, args) is { } userMatch)
            yield return userMatch;

        var resource = "rules/" + fileName;
        using var stream = typeof(RuleStore).Assembly.GetManifestResourceStream(resource);
        if (stream is not null && MatchRules(Load(stream, resource), RuleSource.Embedded, resource, args) is { } embedded)
            yield return embedded;
    }

    /// <summary>The project rule file for a command, whether trusted or not. Used by explain.</summary>
    public static string? ProjectFile(string command, string cwd)
    {
        var name = CommandName(command);
        if (name.Length == 0 || Paths.FindProjectRulesDir(cwd) is not { } dir) return null;
        var path = Path.Combine(dir, name + ".json");
        return File.Exists(path) ? path : null;
    }

    public static IEnumerable<(string File, Rule Rule)> Embedded()
    {
        var assembly = typeof(RuleStore).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith("rules/", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            foreach (var rule in Load(stream, resource).Rules) yield return (resource, rule);
        }
    }

    public static RuleFile LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, path);
    }

    static RuleFile Load(Stream stream, string origin)
    {
        try
        {
            var file = JsonSerializer.Deserialize(stream, CpxJson.Default.RuleFile)
                       ?? throw new RuleException($"{origin}: empty rule file");
            for (var i = 0; i < file.Rules.Count; i++)
                if (string.IsNullOrEmpty(file.Rules[i].Name))
                    throw new RuleException($"{origin}: rules[{i}] has no name");
            return file;
        }
        catch (JsonException e)
        {
            throw new RuleException($"{origin}: {e.Message}");
        }
    }

    /// <summary>On the command path a broken rule file must not block the command: warn and behave as if it had no match.</summary>
    static RuleMatch? MatchFile(string path, RuleSource source, string[] args)
    {
        try
        {
            return MatchRules(LoadFile(path), source, path, args);
        }
        catch (Exception e) when (e is RuleException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cpx] ignoring rules: {e.Message}");
            return null;
        }
    }

    static RuleMatch? MatchRules(RuleFile file, RuleSource source, string origin, string[] args)
    {
        foreach (var rule in file.Rules)
        {
            var insertAt = Match(rule, args);
            if (insertAt >= 0) return new RuleMatch(rule, source, origin, insertAt);
        }
        return null;
    }

    /// <summary>Index where addArgs go (right after the matched prefix), or -1 when the rule does not match.</summary>
    internal static int Match(Rule rule, string[] args)
    {
        if (rule.Unless is { } unless)
            foreach (var arg in args)
                foreach (var prefix in unless)
                    if (arg.StartsWith(prefix, StringComparison.Ordinal)) return -1;

        var insertAt = 0;
        var matched = 0;
        for (var i = 0; matched < rule.Args.Length; i++)
        {
            if (i == args.Length) return -1;
            var arg = args[i];
            if (arg == "--") return -1;
            if (arg.StartsWith('-')) continue;
            if (arg != rule.Args[matched]) return -1;
            matched++;
            insertAt = i + 1;
        }
        return insertAt;
    }
}
