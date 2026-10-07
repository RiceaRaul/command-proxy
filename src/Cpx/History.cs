using System.Buffers;
using System.Text.Json;
using Cpx.Rules;

namespace Cpx;

/// <summary>Append-only JSONL log of filtered runs, and the `gain` report over it.</summary>
static class History
{
    const int MaxCommandLength = 200;
    const long RotateAboveBytes = 5 * 1024 * 1024;

    public static void Append(string command, string[] args, string rule, long inBytes, long outBytes, long ms, int exitCode)
    {
        try
        {
            var text = args.Length == 0 ? command : command + " " + string.Join(' ', args);
            if (text.Length > MaxCommandLength) text = text[..MaxCommandLength];
            var entry = new HistoryEntry
            {
                Ts = DateTimeOffset.UtcNow, Cmd = text, Rule = rule, In = inBytes, Out = outBytes, Ms = ms, Exit = exitCode,
            };

            // One buffer, one write: concurrent cpx processes append whole lines.
            var line = new ArrayBufferWriter<byte>(256);
            using (var writer = new Utf8JsonWriter(line))
                JsonSerializer.Serialize(writer, entry, CpxJson.Default.HistoryEntry);
            line.Write("\n"u8);

            Directory.CreateDirectory(Paths.DataDir);
            using var file = new FileStream(Paths.HistoryFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 0);
            file.Write(line.WrittenSpan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Statistics must never make the command fail.
        }
    }

    public static int Gain(bool showHistory)
    {
        var entries = Read();
        if (entries.Count == 0)
        {
            Console.WriteLine("cpx gain: no filtered commands recorded yet");
            return 0;
        }

        if (showHistory)
        {
            foreach (var entry in entries.Skip(Math.Max(0, entries.Count - 30)))
                Console.WriteLine(
                    $"{entry.Ts.ToLocalTime():MM-dd HH:mm}  {Size(entry.In),8} → {Size(entry.Out),8}  {Percent(entry.In, entry.Out),4}  {entry.Cmd}");
            return 0;
        }

        long totalIn = 0, totalOut = 0;
        var byRule = new Dictionary<string, (int Runs, long In, long Out)>();
        foreach (var entry in entries)
        {
            totalIn += entry.In;
            totalOut += entry.Out;
            var current = byRule.GetValueOrDefault(entry.Rule);
            byRule[entry.Rule] = (current.Runs + 1, current.In + entry.In, current.Out + entry.Out);
        }

        Console.WriteLine(
            $"cpx gain: {entries.Count} commands, {Size(totalIn)} → {Size(totalOut)}, saved {Percent(totalIn, totalOut)} (~{(totalIn - totalOut) / 4:N0} tokens)");
        Console.WriteLine();
        Console.WriteLine($"{"rule",-20} {"runs",5} {"in",9} {"out",9} {"saved",6}");
        foreach (var (rule, stats) in byRule.OrderByDescending(pair => pair.Value.In - pair.Value.Out))
            Console.WriteLine($"{rule,-20} {stats.Runs,5} {Size(stats.In),9} {Size(stats.Out),9} {Percent(stats.In, stats.Out),6}");

        Rotate(entries);
        return 0;
    }

    static List<HistoryEntry> Read()
    {
        var entries = new List<HistoryEntry>();
        if (!File.Exists(Paths.HistoryFile)) return entries;
        foreach (var line in File.ReadLines(Paths.HistoryFile))
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, CpxJson.Default.HistoryEntry) is { } entry) entries.Add(entry);
            }
            catch (JsonException)
            {
                // a torn or hand-edited line; skip it
            }
        }
        return entries;
    }

    /// <summary>Retention happens here, off the command path: keep the newer half once the file is large.</summary>
    static void Rotate(List<HistoryEntry> entries)
    {
        try
        {
            if (new FileInfo(Paths.HistoryFile).Length <= RotateAboveBytes) return;
            var temp = Paths.HistoryFile + ".tmp";
            using (var file = File.Create(temp))
                foreach (var entry in entries.Skip(entries.Count / 2))
                {
                    JsonSerializer.Serialize(file, entry, CpxJson.Default.HistoryEntry);
                    file.Write("\n"u8);
                }
            File.Move(temp, Paths.HistoryFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024):F1} MB",
    };

    static string Percent(long before, long after) =>
        before <= 0 ? "0%" : $"{(before - after) * 100 / before}%";
}
