using System.Buffers;
using System.Text;
using Cpx.Pipeline;
using Cpx.Rules;

namespace Cpx;

/// <summary>The meta commands used to write and debug rules.</summary>
static class Commands
{
    public static int Explain(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: cpx explain <command> [args...]");
            return 2;
        }

        var command = args[0];
        var rest = args[1..];
        var cwd = Environment.CurrentDirectory;
        var candidates = RuleStore.Candidates(command, rest, cwd).ToList();

        Console.WriteLine($"command:  {string.Join(' ', args)}");
        if (RuleStore.ProjectFile(command, cwd) is { } projectFile && !Trust.IsTrusted(projectFile))
            Console.WriteLine($"note:     {projectFile} is not trusted and is ignored (run `cpx trust`)");

        if (candidates.Count == 0)
        {
            Console.WriteLine("rule:     none, the command runs unfiltered");
            return 0;
        }

        var match = candidates[0];
        var rule = match.Rule;
        Console.WriteLine($"rule:     {rule.Name} ({Describe(match)})");
        if (rule.Description is not null) Console.WriteLine($"          {rule.Description}");
        Console.WriteLine($"runs:     {command} {string.Join(' ', match.BuildArgs(rest))}".TrimEnd());
        Console.WriteLine($"stderr:   {(rule.MergeStderr ? "captured, appended after stdout" : "passed through")}");

        var valid = PrintSteps("onSuccess", rule, success: true);
        valid &= PrintSteps("onFailure", rule, success: false);

        foreach (var shadowed in candidates.Skip(1))
            Console.WriteLine($"shadows:  {shadowed.Rule.Name} ({Describe(shadowed)})");
        return valid ? 0 : 1;
    }

    public static int TestRule(string[] args)
    {
        string? inputPath = null;
        var cwd = Environment.CurrentDirectory;
        var exitCode = 0;
        var index = 0;
        for (; index < args.Length; index++)
        {
            if (args[index] == "--input" && index + 1 < args.Length) inputPath = args[++index];
            else if (args[index] == "--cwd" && index + 1 < args.Length) cwd = args[++index];
            else if (args[index] == "--exit" && index + 1 < args.Length && int.TryParse(args[index + 1], out exitCode)) index++;
            else break;
        }

        if (inputPath is null || index >= args.Length)
        {
            Console.Error.WriteLine("usage: cpx test-rule --input <file> [--exit N] [--cwd <dir>] <command> [args...]");
            return 2;
        }

        var command = args[index];
        var rest = args[(index + 1)..];
        if (RuleStore.Find(command, rest, Environment.CurrentDirectory) is not { } match)
        {
            Console.Error.WriteLine($"cpx test-rule: no rule matches `{string.Join(' ', args[index..])}`");
            return 1;
        }

        try
        {
            var raw = File.ReadAllBytes(inputPath);
            var filtered = new ArrayBufferWriter<byte>(4096);
            Proxy.Apply(match.Rule, exitCode, raw, filtered, cwd);

            using var stdout = Console.OpenStandardOutput();
            stdout.Write(filtered.WrittenSpan);
            var saved = raw.Length == 0 ? 0 : (raw.Length - filtered.WrittenCount) * 100L / raw.Length;
            Console.Error.WriteLine($"[cpx] rule {match.Rule.Name}: {raw.Length} → {filtered.WrittenCount} bytes ({saved}% saved)");
            return 0;
        }
        catch (Exception e) when (e is RuleException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cpx test-rule: {e.Message}");
            return 1;
        }
    }

    public static int TrustRules(string[] args)
    {
        if (ProjectRuleFiles() is not { Length: > 0 } files) return 1;

        var valid = true;
        foreach (var file in files)
        {
            Console.WriteLine($"{file}{(Trust.IsTrusted(file) ? "  (already trusted)" : "")}");
            try
            {
                foreach (var rule in RuleStore.LoadFile(file).Rules)
                {
                    Console.WriteLine($"  {rule.Name}: {Path.GetFileNameWithoutExtension(file)} {string.Join(' ', rule.Args)}".TrimEnd());
                    if (rule.AddArgs is { Length: > 0 } extra) Console.WriteLine($"    adds arguments: {string.Join(' ', extra)}");
                    valid &= PrintSteps("    onSuccess", rule, success: true);
                    valid &= PrintSteps("    onFailure", rule, success: false);
                }
            }
            catch (RuleException e)
            {
                Console.WriteLine($"  invalid: {e.Message}");
                valid = false;
            }
        }

        if (!valid)
        {
            Console.Error.WriteLine("cpx trust: fix the errors above first");
            return 1;
        }

        if (!args.Contains("--yes") && !args.Contains("-y"))
        {
            Console.Write("These rules decide what an agent sees of a command's output. Trust them? [y/N] ");
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
            {
                Console.WriteLine("not trusted");
                return 1;
            }
        }

        Trust.Add(files);
        Console.WriteLine($"trusted {files.Length} file(s); editing one revokes its trust");
        return 0;
    }

    public static int UntrustRules()
    {
        if (ProjectRuleFiles() is not { Length: > 0 } files) return 1;
        Console.WriteLine($"revoked trust for {Trust.Remove(files)} file(s)");
        return 0;
    }

    static string[]? ProjectRuleFiles()
    {
        if (Paths.FindProjectRulesDir(Environment.CurrentDirectory) is not { } directory)
        {
            Console.Error.WriteLine($"no {Paths.ProjectDirName}/rules directory here or in a parent directory");
            return null;
        }
        var files = Directory.GetFiles(directory, "*.json");
        if (files.Length == 0) Console.Error.WriteLine($"no rule files in {directory}");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    static string Describe(RuleMatch match) => match.Source switch
    {
        RuleSource.Project => $"project: {match.Origin}",
        RuleSource.User => $"user: {match.Origin}",
        _ => $"embedded: {match.Origin}",
    };

    /// <summary>Prints one pipeline and validates it. Returns false when a step is invalid.</summary>
    static bool PrintSteps(string label, Rule rule, bool success)
    {
        var specs = rule.StepsFor(success);
        Console.WriteLine($"{label}:{(specs.Length == 0 ? " (no steps, output unchanged)" : "")}");
        var indent = new string(' ', label.Length - label.TrimStart().Length + 2);
        for (var i = 0; i < specs.Length; i++) Console.WriteLine($"{indent}{i + 1}. {Describe(specs[i])}");

        try
        {
            Steps.Build(specs, $"{rule.Name}.{label.Trim()}", Environment.CurrentDirectory);
            return true;
        }
        catch (RuleException e)
        {
            Console.WriteLine($"{indent}invalid: {e.Message}");
            return false;
        }
    }

    static string Describe(StepSpec spec)
    {
        var text = new StringBuilder(spec.Step);
        if (spec.Name is not null) text.Append($" {spec.Name}");
        if (spec.N is { } n) text.Append($" n={n}");
        if (spec.Regex is not null) text.Append($" regex=/{spec.Regex}/");
        if (spec.Contains is not null) text.Append($" contains=[{string.Join(", ", spec.Contains.Select(s => $"\"{s}\""))}]");
        if (spec.With is not null) text.Append($" with=\"{spec.With}\"");
        if (spec.Adjacent) text.Append(" adjacent");
        if (spec.Message is not null) text.Append($" message=\"{spec.Message}\"");
        return text.ToString();
    }
}
