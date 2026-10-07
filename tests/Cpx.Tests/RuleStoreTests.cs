using Cpx.Rules;

namespace Cpx.Tests;

public class RuleStoreTests
{
    static Rule Rule(string[] args, string[]? unless = null, string[]? addArgs = null) =>
        new() { Name = "r", Args = args, Unless = unless, AddArgs = addArgs };

    [Theory]
    [InlineData(new[] { "status" }, 1)]
    [InlineData(new[] { "--no-pager", "status", "-v" }, 2)]
    [InlineData(new[] { "diff" }, -1)]
    [InlineData(new string[0], -1)]
    [InlineData(new[] { "-C", "dir", "status" }, -1)] // "dir" is taken for the subcommand: unfiltered, which is the safe outcome
    [InlineData(new[] { "--", "status" }, -1)]
    public void Prefix_matches_positional_arguments(string[] args, int expected) =>
        Assert.Equal(expected, RuleStore.Match(Rule(["status"]), args));

    [Fact]
    public void Multi_token_prefix() =>
        Assert.Equal(2, RuleStore.Match(Rule(["migrations", "add"]), ["migrations", "add", "Init"]));

    [Fact]
    public void Empty_prefix_matches_anything() =>
        Assert.Equal(0, RuleStore.Match(Rule([]), ["pattern", "src"]));

    [Fact]
    public void Unless_blocks_on_argument_prefix()
    {
        var rule = Rule(["status"], unless: ["--porcelain"]);
        Assert.Equal(-1, RuleStore.Match(rule, ["status", "--porcelain=v2"]));
        Assert.Equal(1, RuleStore.Match(rule, ["status", "--verbose"]));
    }

    [Fact]
    public void AddArgs_go_right_after_the_prefix_not_at_the_end()
    {
        var match = new RuleMatch(Rule(["diff"], addArgs: ["--no-color"]), RuleSource.Embedded, "x", 1);
        Assert.Equal(["diff", "--no-color", "--", "file.txt"], match.BuildArgs(["diff", "--", "file.txt"]));
    }

    [Fact]
    public void AddArgs_already_present_are_not_repeated()
    {
        var match = new RuleMatch(Rule(["build"], addArgs: ["-nologo", "-tl:off"]), RuleSource.Embedded, "x", 1);
        Assert.Equal(["build", "-tl:off", "-nologo"], match.BuildArgs(["build", "-nologo"]));
    }

    [Theory]
    [InlineData("git", "git")]
    [InlineData("/usr/bin/git", "git")]
    [InlineData("python3.12", "python3.12")]
    [InlineData("..", "")]
    [InlineData("a b", "")]
    [InlineData("", "")]
    public void Command_name(string command, string expected) =>
        Assert.Equal(expected, RuleStore.CommandName(command));

    [Fact]
    public void Embedded_rule_is_found() =>
        Assert.Equal("dotnet-build", RuleStore.Find("dotnet", ["build", "-c", "Release"], TestEnvironment.NewDirectory())?.Rule.Name);

    [Fact]
    public void Unknown_command_has_no_rule() =>
        Assert.Null(RuleStore.Find("no-such-tool", ["x"], TestEnvironment.NewDirectory()));

    [Fact]
    public void User_rule_shadows_embedded_and_falls_through_when_it_does_not_match()
    {
        var path = Path.Combine(TestEnvironment.ConfigDir, "rules", "tree.json");
        File.WriteAllText(path, """{ "rules": [ { "name": "my-tree", "args": ["special"], "steps": [] } ] }""");
        try
        {
            var cwd = TestEnvironment.NewDirectory();
            var special = RuleStore.Candidates("tree", ["special"], cwd).ToList();
            Assert.Equal(["my-tree", "tree"], special.Select(m => m.Rule.Name));
            Assert.Equal(RuleSource.User, special[0].Source);
            Assert.Equal("tree", RuleStore.Find("tree", ["other"], cwd)?.Rule.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Project_rule_applies_only_while_trusted()
    {
        var project = TestEnvironment.NewDirectory();
        var nested = Directory.CreateDirectory(Path.Combine(project, "src", "deep")).FullName;
        var rulesDir = Directory.CreateDirectory(Path.Combine(project, ".cpx", "rules")).FullName;
        var path = Path.Combine(rulesDir, "find.json");
        File.WriteAllText(path, """{ "rules": [ { "name": "project-find", "args": [], "steps": [] } ] }""");

        Assert.Equal("find", RuleStore.Find("find", ["."], nested)?.Rule.Name);

        Trust.Add([path]);
        Assert.Equal("project-find", RuleStore.Find("find", ["."], nested)?.Rule.Name);

        File.AppendAllText(path, "\n");
        Assert.Equal("find", RuleStore.Find("find", ["."], nested)?.Rule.Name);

        Trust.Add([path]);
        Assert.Equal(1, Trust.Remove([path]));
        Assert.Equal("find", RuleStore.Find("find", ["."], nested)?.Rule.Name);
    }

    [Fact]
    public void Unknown_property_in_a_rule_file_is_an_error()
    {
        var path = Path.Combine(TestEnvironment.NewDirectory(), "x.json");
        File.WriteAllText(path, """{ "rules": [ { "name": "x", "arg": ["typo"] } ] }""");
        var error = Assert.Throws<RuleException>(() => RuleStore.LoadFile(path));
        Assert.Contains("arg", error.Message);
    }
}
