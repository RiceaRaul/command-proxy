using System.Buffers;
using System.Text;
using Cpx.Rules;

namespace Cpx.Tests;

public class StepsTests
{
    static string Run(string input, int exitCode, params StepSpec[] steps)
    {
        var rule = new Rule { Name = "test", Steps = steps };
        var output = new ArrayBufferWriter<byte>();
        Proxy.Apply(rule, exitCode, Encoding.UTF8.GetBytes(input), output, "/work/app");
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    static string Run(string input, params StepSpec[] steps) => Run(input, 0, steps);

    [Fact]
    public void No_steps_only_normalises_line_ends_and_blank_edges() =>
        Assert.Equal("a\n\nb\n", Run("\n\na  \r\n\nb\n\n\n"));

    [Fact]
    public void Carriage_return_progress_keeps_the_last_version() =>
        Assert.Equal("100%\ndone\n", Run("10%\r50%\r100%\ndone\n"));

    [Fact]
    public void StripAnsi_removes_colours_and_cursor_codes() =>
        Assert.Equal("red plain\n", Run("\x1b[31mred\x1b[0m plain\x1b[K\n", new StepSpec { Step = "stripAnsi" }));

    [Fact]
    public void Keep_with_literals() =>
        Assert.Equal("x: error 1\ny: warning 2\n",
            Run("x: error 1\nnoise\ny: warning 2\n", new StepSpec { Step = "keep", Contains = [": error ", ": warning "] }));

    [Fact]
    public void Keep_with_regex() =>
        Assert.Equal("b2\n", Run("a\nb2\nc\n", new StepSpec { Step = "keep", Regex = @"\d$" }));

    [Fact]
    public void Drop_with_regex() =>
        Assert.Equal("a\nc\n", Run("a\nb2\nc\n", new StepSpec { Step = "drop", Regex = @"\d$" }));

    [Fact]
    public void DropBlank_removes_inner_blank_lines() =>
        Assert.Equal("a\nb\n", Run("a\n\n   \nb\n", new StepSpec { Step = "dropBlank" }));

    [Fact]
    public void Replace_supports_groups() =>
        Assert.Equal("b-a\nplain\n", Run("a-b\nplain\n", new StepSpec { Step = "replace", Regex = @"^(\w)-(\w)$", With = "$2-$1" }));

    [Fact]
    public void RelativePaths_strips_the_working_directory() =>
        Assert.Equal("src/A.cs(1,2): error in src/B.cs\n/work/application/C.cs\n",
            Run("/work/app/src/A.cs(1,2): error in /work/app/src/B.cs\n/work/application/C.cs\n", new StepSpec { Step = "relativePaths" }));

    [Fact]
    public void Dedupe_is_global_by_default() =>
        Assert.Equal("a\nb\n", Run("a\nb\na\nb\n", new StepSpec { Step = "dedupe" }));

    [Fact]
    public void Dedupe_adjacent_keeps_separated_repeats() =>
        Assert.Equal("a\nb\na\n", Run("a\na\nb\na\n", new StepSpec { Step = "dedupe", Adjacent = true }));

    [Fact]
    public void TruncateLine_marks_the_cut() =>
        Assert.Equal("abc…\nab\n", Run("abcdef\nab\n", new StepSpec { Step = "truncateLine", N = 3 }));

    [Fact]
    public void Head_and_tail() =>
        Assert.Equal("2\n3\n", Run("1\n2\n3\n4\n", new StepSpec { Step = "head", N = 3 }, new StepSpec { Step = "tail", N = 2 }));

    [Fact]
    public void MaxLines_reports_what_was_cut() =>
        Assert.Equal("1\n2\n… +3 lines\n", Run("1\n2\n3\n4\n5\n", new StepSpec { Step = "maxLines", N = 2 }));

    [Fact]
    public void MaxLines_leaves_short_output_alone() =>
        Assert.Equal("1\n2\n", Run("1\n2\n", new StepSpec { Step = "maxLines", N = 2 }));

    [Fact]
    public void MatchOutput_replaces_everything_and_stops() =>
        Assert.Equal("ok\n",
            Run("a\nBuild succeeded.\nb\n",
                new StepSpec { Step = "matchOutput", Contains = ["Build succeeded"], Message = "ok" },
                new StepSpec { Step = "keep", Contains = ["never reached"] }));

    [Fact]
    public void OnEmpty_only_fires_when_nothing_is_left() =>
        Assert.Equal("ok\nkept\n",
            Run("noise\n", new StepSpec { Step = "drop", Contains = ["noise"] }, new StepSpec { Step = "onEmpty", Message = "ok" })
            + Run("kept\n", new StepSpec { Step = "onEmpty", Message = "ok" }));

    [Fact]
    public void Failing_command_is_never_silenced() =>
        Assert.Equal("some unexpected failure\n",
            Run("some unexpected failure\n", exitCode: 1, new StepSpec { Step = "keep", Contains = [": error "] }));

    [Fact]
    public void Successful_command_may_end_up_empty() =>
        Assert.Equal("", Run("noise\n", new StepSpec { Step = "keep", Contains = [": error "] }));

    [Fact]
    public void Invalid_utf8_does_not_throw() =>
        Assert.Contains("ok", Run("ok \xff\xfe\n"));

    [Theory]
    [InlineData("nope", null, null, null, "unknown step")]
    [InlineData("keep", null, null, null, "needs 'regex' or 'contains'")]
    [InlineData("keep", "(", null, null, "invalid regex")]
    [InlineData("replace", "a", null, null, "needs 'with'")]
    [InlineData("head", null, null, null, "needs 'n'")]
    [InlineData("onEmpty", null, null, null, "needs 'message'")]
    [InlineData("builtin", null, null, "nope", "unknown builtin")]
    public void Invalid_steps_are_reported(string step, string? regex, string? with, string? name, string expected)
    {
        var spec = new StepSpec { Step = step, Regex = regex, With = with, Name = name };
        var error = Assert.Throws<RuleException>(() => Run("x\n", spec));
        Assert.Contains(expected, error.Message);
        Assert.Contains("test.onSuccess[0]", error.Message);
    }
}
