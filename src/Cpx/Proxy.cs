using System.Buffers;
using System.Diagnostics;
using System.Text;
using Cpx.Pipeline;
using Cpx.Rules;

namespace Cpx;

static class Proxy
{
    const int MinSavingForRawLog = 256;
    const int RawLogsKept = 20;

    public static int Run(string command, string[] args, bool raw)
    {
        var match = raw ? null : RuleStore.Find(command, args, Environment.CurrentDirectory);
        if (match is null) return Runner.Passthrough(command, args);

        var started = Stopwatch.GetTimestamp();
        var captured = new ArrayBufferWriter<byte>(16 * 1024);
        if (Runner.Capture(command, match.BuildArgs(args), match.Rule.MergeStderr, captured) is not { } exitCode)
            return Runner.NotFound;

        using var stdout = Console.OpenStandardOutput();
        var filtered = new ArrayBufferWriter<byte>(4096);
        try
        {
            Apply(match.Rule, exitCode, captured.WrittenSpan, filtered, Environment.CurrentDirectory);
        }
        catch (Exception e)
        {
            // A broken rule must never hide what the command said.
            stdout.Write(captured.WrittenSpan);
            Console.Error.WriteLine($"[cpx] rule '{match.Rule.Name}' failed, output not filtered: {e.Message}");
            return exitCode;
        }

        if (exitCode != 0
            && captured.WrittenCount - filtered.WrittenCount >= MinSavingForRawLog
            && SaveRaw(RuleStore.CommandName(command), captured.WrittenSpan) is { } path)
        {
            Encoding.UTF8.GetBytes($"[cpx] full output: {path}\n", filtered);
        }

        stdout.Write(filtered.WrittenSpan);
        History.Append(command, args, match.Rule.Name, captured.WrittenCount, filtered.WrittenCount,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, exitCode);
        return exitCode;
    }

    /// <summary>Runs the rule's pipeline for this exit code over raw UTF-8 output and writes the result as UTF-8.</summary>
    public static void Apply(Rule rule, int exitCode, ReadOnlySpan<byte> raw, IBufferWriter<byte> output, string cwd)
    {
        var success = exitCode == 0;
        var steps = Steps.Build(rule.StepsFor(success), $"{rule.Name}.{(success ? "onSuccess" : "onFailure")}", cwd);

        if (raw.StartsWith(Encoding.UTF8.Preamble)) raw = raw[Encoding.UTF8.Preamble.Length..];

        // UTF-8 never decodes to more chars than it has bytes.
        var chars = ArrayPool<char>.Shared.Rent(Math.Max(raw.Length, 1));
        try
        {
            var length = Encoding.UTF8.GetChars(raw, chars);
            var text = chars.AsMemory(0, length);
            var buffer = LineBuffer.Parse(text);
            Steps.Run(steps, buffer);

            // A failing command must never come out silent: if the rule removed everything, show it all.
            if (!success && buffer.Lines.Count == 0)
            {
                buffer = LineBuffer.Parse(text);
                buffer.TrimBlankEdges();
            }

            foreach (var line in buffer.Lines)
            {
                Encoding.UTF8.GetBytes(line.Span, output);
                output.Write("\n"u8);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    static string? SaveRaw(string name, ReadOnlySpan<byte> raw)
    {
        try
        {
            Directory.CreateDirectory(Paths.RawDir);
            var path = Path.Combine(Paths.RawDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{name}.log");
            using (var file = File.Create(path)) file.Write(raw);

            var logs = Directory.GetFiles(Paths.RawDir, "*.log");
            if (logs.Length > RawLogsKept)
            {
                Array.Sort(logs, StringComparer.Ordinal);
                foreach (var old in logs.AsSpan(0, logs.Length - RawLogsKept)) File.Delete(old);
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
