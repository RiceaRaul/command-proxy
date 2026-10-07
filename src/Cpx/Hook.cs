using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cpx.Rules;

namespace Cpx;

/// <summary>
/// PreToolUse hook shared by Claude Code and Copilot CLI: rewrites `git status` to `cpx git status`
/// when a rule exists for it. Anything it does not fully understand is left alone.
/// </summary>
static class Hook
{
    public const string HookCommand = "cpx hook";

    public static readonly string[] MetaCommands = ["explain", "test-rule", "trust", "untrust", "gain", "hook", "init"];

    // A command containing any of these needs a real shell parser to rewrite safely.
    static readonly SearchValues<char> ShellSyntax = SearchValues.Create("|&;<>()$`\\\n\r");

    static readonly string[] ShellTools = ["Bash", "bash", "powershell", "run_in_terminal", "runTerminalCommand"];

    public static int Run()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var payload = JsonDocument.Parse(stdin);
            if (Respond(payload.RootElement) is { } response)
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(response.WrittenSpan);
            }
        }
        catch (Exception)
        {
            // A hook that fails must not get in the agent's way: say nothing, change nothing.
        }
        return 0;
    }

    /// <summary>
    /// Two payload shapes exist. Claude Code, and Copilot CLI for hooks registered as "PreToolUse",
    /// send tool_name / tool_input and take the rewrite as hookSpecificOutput.updatedInput.
    /// Copilot CLI hooks registered as "preToolUse" send toolName / toolArgs and take modifiedArgs.
    /// No permissionDecision is emitted in either: the agent's own permission flow stays in charge.
    /// </summary>
    internal static ArrayBufferWriter<byte>? Respond(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var snakeCase = root.TryGetProperty("tool_input", out var input);
        JsonDocument? parsedArgs = null;
        try
        {
            if (!snakeCase)
            {
                if (!root.TryGetProperty("toolArgs", out input)) return null;
                // Documented as an object; older Copilot CLI builds sent it as a JSON-encoded string.
                if (input.ValueKind == JsonValueKind.String)
                {
                    parsedArgs = JsonDocument.Parse(input.GetString()!);
                    input = parsedArgs.RootElement;
                }
            }

            if (root.TryGetProperty(snakeCase ? "tool_name" : "toolName", out var tool)
                && (tool.ValueKind != JsonValueKind.String || Array.IndexOf(ShellTools, tool.GetString()) < 0))
                return null;

            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                return null;

            var cwd = root.TryGetProperty("cwd", out var dir) && dir.ValueKind == JsonValueKind.String
                      && Directory.Exists(dir.GetString())
                ? dir.GetString()!
                : Environment.CurrentDirectory;

            if (Rewrite(command.GetString()!, cwd) is not { } rewritten) return null;

            var response = new ArrayBufferWriter<byte>(256);
            using var writer = new Utf8JsonWriter(response);
            writer.WriteStartObject();
            if (snakeCase)
            {
                writer.WriteStartObject("hookSpecificOutput");
                writer.WriteString("hookEventName", "PreToolUse");
            }

            // Everything the tool was given is passed back; only the command changes.
            writer.WriteStartObject(snakeCase ? "updatedInput" : "modifiedArgs");
            foreach (var property in input.EnumerateObject())
            {
                if (property.NameEquals("command")) writer.WriteString("command", rewritten);
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();

            if (snakeCase) writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            return response;
        }
        finally
        {
            parsedArgs?.Dispose();
        }
    }

    /// <summary>The command prefixed with cpx, or null when it should run untouched.</summary>
    internal static string? Rewrite(string command, string cwd)
    {
        command = command.Trim();
        if (command.Length == 0 || command.AsSpan().ContainsAny(ShellSyntax)) return null;
        if (Tokenize(command) is not { Count: > 0 } tokens) return null;

        var first = tokens[0];
        if (first.Contains('=')) return null;
        var name = RuleStore.CommandName(first);
        if (name is "" or "cpx" or "rtk") return null;

        if (RuleStore.Find(first, tokens.GetRange(1, tokens.Count - 1).ToArray(), cwd) is null) return null;
        return Array.IndexOf(MetaCommands, name) >= 0 ? "cpx -- " + command : "cpx " + command;
    }

    /// <summary>Whitespace split that honours '...' and "...". Null on an unterminated quote.</summary>
    internal static List<string>? Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        var quote = '\0';

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) tokens.Add(current.ToString());
                current.Clear();
                inToken = false;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote != '\0') return null;
        if (inToken) tokens.Add(current.ToString());
        return tokens;
    }

    // ---- init -------------------------------------------------------------

    public static int Init(string[] args)
    {
        var remove = args.Contains("--remove");
        var agent = args.FirstOrDefault(arg => !arg.StartsWith('-'));
        switch (agent)
        {
            case "claude":
                return InitClaude(remove);
            case "copilot":
                return InitCopilot(remove);
            default:
                Console.Error.WriteLine("usage: cpx init <claude|copilot> [--remove]");
                return 2;
        }
    }

    static int InitClaude(bool remove)
    {
        var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Paths.Home, ".claude");
        var path = Path.Combine(directory, "settings.json");

        JsonObject settings;
        try
        {
            settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [] : [];
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"cpx init: {path} is not valid JSON, not touching it: {e.Message}");
            return 1;
        }

        var hooks = settings["hooks"] as JsonObject;
        var preToolUse = hooks?["PreToolUse"] as JsonArray;
        var installed = false;
        var rtkPresent = false;

        if (preToolUse is not null)
            for (var g = preToolUse.Count - 1; g >= 0; g--)
            {
                if (preToolUse[g]?["hooks"] is not JsonArray entries) continue;
                for (var h = entries.Count - 1; h >= 0; h--)
                {
                    var command = (entries[h]?["command"] as JsonValue)?.GetValueKind() == JsonValueKind.String
                        ? entries[h]!["command"]!.GetValue<string>()
                        : null;
                    if (command is null) continue;
                    if (command.StartsWith("rtk hook", StringComparison.Ordinal)) rtkPresent = true;
                    if (command != HookCommand) continue;
                    installed = true;
                    if (remove) entries.RemoveAt(h);
                }
                if (remove && entries.Count == 0) preToolUse.RemoveAt(g);
            }

        if (remove)
        {
            if (!installed)
            {
                Console.WriteLine($"cpx hook was not installed in {path}");
                return 0;
            }
            if (preToolUse!.Count == 0) hooks!.Remove("PreToolUse");
            if (hooks!.Count == 0) settings.Remove("hooks");
        }
        else
        {
            if (installed)
            {
                Console.WriteLine($"cpx hook already installed in {path}");
                return 0;
            }
            if (hooks is null) settings["hooks"] = hooks = [];
            if (preToolUse is null) hooks["PreToolUse"] = preToolUse = [];
            // Typed as JsonNode so the non-generic, trim-safe Add overload is chosen.
            JsonNode group = new JsonObject
            {
                ["matcher"] = "Bash",
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = HookCommand }),
            };
            preToolUse.Add(group);
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Copy(path, path + ".cpx-bak", overwrite: true);
        using (var file = File.Create(path))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions
               {
                   Indented = true,
                   // Keep quotes and non-ASCII text readable instead of \uXXXX; this is a config file, not HTML.
                   Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
            settings.WriteTo(writer);

        Console.WriteLine(remove ? $"cpx hook removed from {path}" : $"cpx hook installed in {path}");
        if (File.Exists(path + ".cpx-bak")) Console.WriteLine($"previous version saved as {path}.cpx-bak");
        if (!remove && rtkPresent)
            Console.WriteLine(
                "warning: an rtk hook is also installed. Two hooks rewriting the same command conflict;\n" +
                "         exclude the commands cpx handles in rtk's config (hooks.exclude_commands) or remove the rtk hook.");
        if (!remove) Console.WriteLine("Restart Claude Code to apply.");
        return 0;
    }

    static int InitCopilot(bool remove)
    {
        var directory = Path.Combine(
            Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Paths.Home, ".copilot"), "hooks");
        var path = Path.Combine(directory, "cpx-rewrite.json");

        if (remove)
        {
            var existed = File.Exists(path);
            if (existed) File.Delete(path);
            Console.WriteLine(existed ? $"removed {path}" : $"cpx hook was not installed in {directory}");
            return 0;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(path,
            """
            {
              "version": 1,
              "hooks": {
                "PreToolUse": [
                  { "type": "command", "command": "cpx hook", "cwd": ".", "timeout": 5 }
                ]
              }
            }

            """);
        Console.WriteLine($"cpx hook installed in {path}");
        Console.WriteLine("Restart Copilot CLI to apply. Transparent rewrite needs Copilot CLI 1.0.73 or newer.");
        return 0;
    }
}
