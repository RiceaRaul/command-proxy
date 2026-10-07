using System.Buffers;
using System.Text.RegularExpressions;
using Cpx.Rules;

namespace Cpx.Pipeline;

interface IStep
{
    /// <summary>Returns false to stop the pipeline.</summary>
    bool Apply(LineBuffer buffer);
}

/// <summary>
/// Either a regex or a set of literal substrings. Rule regexes run interpreted under Native AOT
/// (no runtime code generation), so literals via SearchValues are the fast path.
/// </summary>
sealed class LineMatcher
{
    static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    readonly Regex? _regex;
    readonly SearchValues<string>? _literals;

    LineMatcher(Regex? regex, SearchValues<string>? literals) => (_regex, _literals) = (regex, literals);

    public static LineMatcher Create(StepSpec spec, string where)
    {
        if (spec.Regex is not null && spec.Contains is not null)
            throw new RuleException($"{where}: use either 'regex' or 'contains', not both");
        if (spec.Regex is { } pattern) return new LineMatcher(CompileRegex(pattern, where), null);
        if (spec.Contains is { Length: > 0 } literals)
        {
            if (Array.Exists(literals, string.IsNullOrEmpty))
                throw new RuleException($"{where}: 'contains' has an empty entry");
            return new LineMatcher(null, SearchValues.Create(literals, StringComparison.Ordinal));
        }
        throw new RuleException($"{where}: needs 'regex' or 'contains'");
    }

    public static Regex CompileRegex(string pattern, string where)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, Timeout);
        }
        catch (ArgumentException e)
        {
            throw new RuleException($"{where}: invalid regex: {e.Message}");
        }
    }

    public bool IsMatch(ReadOnlySpan<char> line) =>
        _regex is not null ? _regex.IsMatch(line) : line.ContainsAny(_literals!);
}

static partial class Steps
{
    public static IStep[] Build(StepSpec[] specs, string where, string cwd)
    {
        var steps = new IStep[specs.Length];
        for (var i = 0; i < specs.Length; i++) steps[i] = Create(specs[i], $"{where}[{i}] ({specs[i].Step})", cwd);
        return steps;
    }

    public static void Run(IStep[] steps, LineBuffer buffer)
    {
        foreach (var step in steps)
            if (!step.Apply(buffer)) break;
        buffer.TrimBlankEdges();
    }

    static IStep Create(StepSpec spec, string where, string cwd) => spec.Step switch
    {
        "stripAnsi" => new StripAnsi(),
        "relativePaths" => new RelativePaths(cwd),
        "dropBlank" => new DropBlank(),
        "keep" => new Select(LineMatcher.Create(spec, where), keep: true),
        "drop" => new Select(LineMatcher.Create(spec, where), keep: false),
        "replace" => new Replace(
            LineMatcher.CompileRegex(spec.Regex ?? throw new RuleException($"{where}: needs 'regex'"), where),
            spec.With ?? throw new RuleException($"{where}: needs 'with'")),
        "dedupe" => new Dedupe(spec.Adjacent),
        "truncateLine" => new TruncateLine(Count(spec, where)),
        "head" => new Head(Count(spec, where)),
        "tail" => new Tail(Count(spec, where)),
        "maxLines" => new MaxLines(Count(spec, where)),
        "matchOutput" => new MatchOutput(LineMatcher.Create(spec, where), Message(spec, where)),
        "onEmpty" => new OnEmpty(Message(spec, where)),
        "builtin" => spec.Name switch
        {
            "dotnet-test" => new DotnetTestFilter(),
            null => throw new RuleException($"{where}: needs 'name'"),
            var name => throw new RuleException($"{where}: unknown builtin '{name}'"),
        },
        "" => throw new RuleException($"{where}: missing 'step'"),
        _ => throw new RuleException($"{where}: unknown step"),
    };

    static int Count(StepSpec spec, string where) =>
        spec.N is > 0 and var n ? n : throw new RuleException($"{where}: needs 'n' greater than 0");

    static string Message(StepSpec spec, string where) =>
        spec.Message ?? throw new RuleException($"{where}: needs 'message'");

    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]")]
    private static partial Regex AnsiEscape();

    sealed class StripAnsi : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            var lines = buffer.Lines;
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Span.Contains('\x1b'))
                    lines[i] = AnsiEscape().Replace(lines[i].ToString(), "").AsMemory().TrimEnd();
            return true;
        }
    }

    /// <summary>Absolute paths under the working directory become relative; tools like MSBuild print them in full.</summary>
    sealed class RelativePaths(string cwd) : IStep
    {
        readonly string _prefix = Path.EndsInDirectorySeparator(cwd) ? cwd : cwd + Path.DirectorySeparatorChar;

        public bool Apply(LineBuffer buffer)
        {
            var lines = buffer.Lines;
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Span.Contains(_prefix, StringComparison.Ordinal))
                    lines[i] = lines[i].ToString().Replace(_prefix, "", StringComparison.Ordinal).AsMemory();
            return true;
        }
    }

    sealed class DropBlank : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            buffer.Lines.RemoveAll(static line => line.Span.IsWhiteSpace());
            return true;
        }
    }

    sealed class Select(LineMatcher matcher, bool keep) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            buffer.Lines.RemoveAll(line => matcher.IsMatch(line.Span) != keep);
            return true;
        }
    }

    sealed class Replace(Regex regex, string replacement) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            var lines = buffer.Lines;
            for (var i = 0; i < lines.Count; i++)
                if (regex.IsMatch(lines[i].Span))
                    lines[i] = regex.Replace(lines[i].ToString(), replacement).AsMemory().TrimEnd();
            return true;
        }
    }

    sealed class Dedupe(bool adjacent) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            var lines = buffer.Lines;
            if (adjacent)
            {
                var kept = 0;
                for (var i = 0; i < lines.Count; i++)
                    if (kept == 0 || !lines[i].Span.SequenceEqual(lines[kept - 1].Span))
                        lines[kept++] = lines[i];
                lines.RemoveRange(kept, lines.Count - kept);
            }
            else
            {
                var seen = new HashSet<ReadOnlyMemory<char>>(lines.Count, SpanComparer.Instance);
                lines.RemoveAll(line => !seen.Add(line));
            }
            return true;
        }
    }

    /// <summary>Compares slices by content, so deduplication needs no string per line.</summary>
    sealed class SpanComparer : IEqualityComparer<ReadOnlyMemory<char>>
    {
        public static readonly SpanComparer Instance = new();
        public bool Equals(ReadOnlyMemory<char> x, ReadOnlyMemory<char> y) => x.Span.SequenceEqual(y.Span);
        public int GetHashCode(ReadOnlyMemory<char> value) => string.GetHashCode(value.Span);
    }

    sealed class TruncateLine(int max) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            var lines = buffer.Lines;
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Length > max)
                    lines[i] = string.Concat(lines[i].Span[..max], "…").AsMemory();
            return true;
        }
    }

    sealed class Head(int count) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            if (buffer.Lines.Count > count) buffer.Lines.RemoveRange(count, buffer.Lines.Count - count);
            return true;
        }
    }

    sealed class Tail(int count) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            if (buffer.Lines.Count > count) buffer.Lines.RemoveRange(0, buffer.Lines.Count - count);
            return true;
        }
    }

    sealed class MaxLines(int max) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            var extra = buffer.Lines.Count - max;
            if (extra > 0)
            {
                buffer.Lines.RemoveRange(max, extra);
                buffer.Lines.Add($"… +{extra} lines".AsMemory());
            }
            return true;
        }
    }

    sealed class MatchOutput(LineMatcher matcher, string message) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            foreach (var line in buffer.Lines)
                if (matcher.IsMatch(line.Span))
                {
                    buffer.ReplaceAll(message);
                    return false;
                }
            return true;
        }
    }

    sealed class OnEmpty(string message) : IStep
    {
        public bool Apply(LineBuffer buffer)
        {
            foreach (var line in buffer.Lines)
                if (!line.Span.IsWhiteSpace()) return true;
            buffer.ReplaceAll(message);
            return true;
        }
    }
}
