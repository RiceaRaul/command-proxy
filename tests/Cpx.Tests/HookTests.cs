namespace Cpx.Tests;

public class HookTests
{
    static string? Rewrite(string command) => Hook.Rewrite(command, TestEnvironment.NewDirectory());

    [Theory]
    [InlineData("git status", "cpx git status")]
    [InlineData("  dotnet build -c Release ", "cpx dotnet build -c Release")]
    [InlineData("git commit -m \"fix the status page\"", "cpx git commit -m \"fix the status page\"")]
    [InlineData("/usr/bin/git diff", "cpx /usr/bin/git diff")]
    public void Simple_commands_with_a_rule_are_prefixed(string command, string expected) =>
        Assert.Equal(expected, Rewrite(command));

    [Theory]
    [InlineData("")]
    [InlineData("echo hello")]                    // no rule
    [InlineData("git stash")]                     // no rule for this subcommand
    [InlineData("git status --porcelain")]        // excluded by the rule's 'unless'
    [InlineData("cd src && git status")]          // compound
    [InlineData("git status | head")]
    [InlineData("git log > out.txt")]
    [InlineData("git diff $(git merge-base a b)")]
    [InlineData("FOO=1 git status")]
    [InlineData("cpx git status")]
    [InlineData("rtk git status")]
    [InlineData("git commit -m \"unterminated")]
    public void Everything_else_is_left_alone(string command) =>
        Assert.Null(Rewrite(command));

    [Fact]
    public void Tokenize_honours_quotes() =>
        Assert.Equal(["git", "commit", "-m", "two words", "it's"], Hook.Tokenize("git commit -m 'two words' \"it's\""));

    static string? Respond(string payload)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payload);
        return Hook.Respond(document.RootElement) is { } response
            ? System.Text.Encoding.UTF8.GetString(response.WrittenSpan)
            : null;
    }

    // Payloads below were captured from Copilot CLI 1.0.93 (session id and cwd shortened).

    [Fact]
    public void Copilot_PreToolUse_payload_gets_updatedInput() =>
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"cpx git status","description":"Check repository status","mode":"sync","initial_wait":30}}}""",
            Respond("""{"hook_event_name":"PreToolUse","session_id":"s","timestamp":"2026-10-07T14:56:17.245Z","cwd":"/nonexistent","tool_name":"Bash","tool_input":{"command":"git status","description":"Check repository status","mode":"sync","initial_wait":30}}"""));

    [Fact]
    public void Copilot_preToolUse_payload_gets_modifiedArgs() =>
        Assert.Equal(
            """{"modifiedArgs":{"command":"cpx git status","description":"Check repository status","mode":"sync","initial_wait":30}}""",
            Respond("""{"sessionId":"s","timestamp":1791384977245,"cwd":"/nonexistent","toolName":"bash","toolArgs":{"command":"git status","description":"Check repository status","mode":"sync","initial_wait":30}}"""));

    [Fact]
    public void ToolArgs_as_a_json_string_is_accepted() =>
        Assert.Equal("""{"modifiedArgs":{"command":"cpx dotnet build"}}""",
            Respond("""{"toolName":"bash","toolArgs":"{\"command\":\"dotnet build\"}"}"""));

    [Theory]
    [InlineData("""{"toolName":"view","toolArgs":{"command":"git status"}}""")]
    [InlineData("""{"tool_name":"Read","tool_input":{"command":"git status"}}""")]
    [InlineData("""{"toolName":"bash","toolArgs":{"command":"echo hi"}}""")]
    [InlineData("""{"toolName":"bash","toolArgs":"not json"}""")]
    [InlineData("""{"toolName":"bash"}""")]
    [InlineData("""[]""")]
    public void Other_tools_and_odd_payloads_get_no_answer(string payload)
    {
        try
        {
            Assert.Null(Respond(payload));
        }
        catch (System.Text.Json.JsonException)
        {
            // Hook.Run swallows this and stays silent; reaching here is equivalent.
        }
    }
}
