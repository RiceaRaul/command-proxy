using System.Diagnostics;

namespace Cpx.Tests;

/// <summary>Runs the real cpx entry point as a child process. Uses sh, so Unix only.</summary>
public class IntegrationTests
{
    static readonly string CpxDll = Path.Combine(AppContext.BaseDirectory, "cpx.dll");

    sealed record Result(int ExitCode, string Stdout, string Stderr);

    static Result Cpx(string? configDir, string? stdin, params string[] args) => Cpx(configDir, stdin, null, args);

    static Result Cpx(string? configDir, string? stdin, string? claudeDir, string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = TestEnvironment.NewDirectory(),
        };
        info.ArgumentList.Add(CpxDll);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["CPX_CONFIG_DIR"] = configDir ?? TestEnvironment.NewDirectory();
        info.Environment["CPX_DATA_DIR"] = TestEnvironment.NewDirectory();
        // Never let a test reach the real agent configuration.
        info.Environment["CLAUDE_CONFIG_DIR"] = claudeDir ?? TestEnvironment.NewDirectory();
        info.Environment["COPILOT_HOME"] = TestEnvironment.NewDirectory();

        using var process = Process.Start(info)!;
        if (stdin is not null) process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new Result(process.ExitCode, stdout, stderr.Result);
    }

    static string ConfigWithShRule(string steps)
    {
        var config = TestEnvironment.NewDirectory();
        Directory.CreateDirectory(Path.Combine(config, "rules"));
        File.WriteAllText(Path.Combine(config, "rules", "sh.json"),
            $$"""{ "rules": [ { "name": "sh-test", "args": [], "steps": {{steps}} } ] }""");
        return config;
    }

    const string KeepSteps = """[ { "step": "keep", "contains": ["keep"] } ]""";
    const string Script = "echo keep 1; echo drop; echo 'keep 2'; echo to-stderr >&2; exit 3";

    [Fact]
    public void Without_a_rule_output_and_exit_code_pass_through()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = Cpx(null, null, "sh", "-c", "printf 'a\\n\\n  b  \\n'; echo err >&2; exit 7");
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("a\n\n  b  \n", result.Stdout);
        Assert.Equal("err\n", result.Stderr);
    }

    [Fact]
    public void Stdin_reaches_the_command()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("from stdin", Cpx(null, "from stdin", "cat").Stdout);
    }

    [Fact]
    public void Rule_filters_stdout_keeps_exit_code_and_leaves_stderr_alone()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = Cpx(ConfigWithShRule(KeepSteps), null, "sh", "-c", Script);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("keep 1\nkeep 2\n", result.Stdout);
        Assert.Equal("to-stderr\n", result.Stderr);
    }

    [Fact]
    public void Raw_bypasses_the_rule()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = Cpx(ConfigWithShRule(KeepSteps), null, "--raw", "sh", "-c", Script);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("keep 1\ndrop\nkeep 2\n", result.Stdout);
    }

    [Fact]
    public void Broken_rule_shows_raw_output_and_warns()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = Cpx(ConfigWithShRule("""[ { "step": "keep", "regex": "(" } ]"""), null, "sh", "-c", Script);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("keep 1\ndrop\nkeep 2\n", result.Stdout);
        Assert.Contains("output not filtered", result.Stderr);
    }

    [Fact]
    public void Unparseable_rule_file_is_ignored_with_a_warning()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = Cpx(ConfigWithShRule("not json"), null, "sh", "-c", Script);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("keep 1\ndrop\nkeep 2\n", result.Stdout);
        Assert.Contains("ignoring rules", result.Stderr);
    }

    [Fact]
    public void Large_failing_output_is_saved_in_full()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = Cpx(ConfigWithShRule(KeepSteps), null, "sh", "-c", "echo keep; seq 1 500; exit 1");
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("keep", lines[0]);
        Assert.StartsWith("[cpx] full output: ", lines[1]);
        var saved = File.ReadAllText(lines[1]["[cpx] full output: ".Length..]);
        Assert.Contains("\n500\n", saved);
    }

    [Fact]
    public void Missing_command_reports_127()
    {
        var result = Cpx(null, null, "definitely-not-a-command-xyz");
        Assert.Equal(127, result.ExitCode);
        Assert.Equal("cpx: definitely-not-a-command-xyz: command not found\n", result.Stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Hook_rewrites_and_preserves_other_tool_input()
    {
        var payload = """{"tool_name":"Bash","tool_input":{"command":"git status","description":"check","timeout":5000}}""";
        var result = Cpx(null, payload, "hook");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"cpx git status","description":"check","timeout":5000}}}""",
            result.Stdout);
    }

    [Theory]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"echo hi"}}""")]
    [InlineData("""{"tool_name":"Read","tool_input":{"file_path":"/x"}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void Hook_stays_silent_when_it_has_nothing_to_do(string payload)
    {
        var result = Cpx(null, payload, "hook");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
    }

    [Fact]
    public void Init_claude_adds_the_hook_once_and_remove_restores_the_settings()
    {
        var claude = TestEnvironment.NewDirectory();
        var settings = Path.Combine(claude, "settings.json");
        const string original = """
            {
              "hooks": {
                "PreToolUse": [
                  { "matcher": "Bash", "hooks": [ { "type": "command", "command": "\"/opt/x\" run" } ] }
                ]
              },
              "theme": "dark"
            }
            """;
        File.WriteAllText(settings, original);

        Assert.Equal(0, Cpx(null, null, claude, ["init", "claude"]).ExitCode);
        Assert.Contains("already installed", Cpx(null, null, claude, ["init", "claude"]).Stdout);

        var installed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!;
        var groups = installed["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("cpx hook", groups[1]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("dark", installed["theme"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(settings + ".cpx-bak"));

        Assert.Equal(0, Cpx(null, null, claude, ["init", "claude", "--remove"]).ExitCode);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(original),
            System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))));
        Assert.Contains("\\\"/opt/x\\\" run", File.ReadAllText(settings));
    }

    [Fact]
    public void Init_claude_refuses_to_touch_invalid_settings()
    {
        var claude = TestEnvironment.NewDirectory();
        var settings = Path.Combine(claude, "settings.json");
        File.WriteAllText(settings, "{ broken");
        Assert.Equal(1, Cpx(null, null, claude, ["init", "claude"]).ExitCode);
        Assert.Equal("{ broken", File.ReadAllText(settings));
    }
}
