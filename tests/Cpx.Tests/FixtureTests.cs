using System.Buffers;
using System.Text;
using Cpx.Pipeline;
using Cpx.Rules;

namespace Cpx.Tests;

/// <summary>
/// fixtures/&lt;rule&gt;/&lt;exit&gt;-&lt;case&gt;.in.txt is recorded command output;
/// the matching .out.txt is what the embedded rule must turn it into.
/// </summary>
public class FixtureTests
{
    const string FixtureCwd = "/work/app";

    static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "fixtures");

    static readonly Dictionary<string, Rule> EmbeddedRules =
        RuleStore.Embedded().ToDictionary(entry => entry.Rule.Name, entry => entry.Rule);

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var input in Directory.GetFiles(FixtureRoot, "*.in.txt", SearchOption.AllDirectories).Order())
            data.Add(Path.GetFileName(Path.GetDirectoryName(input)!), Path.GetFileName(input)[..^".in.txt".Length]);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Rule_produces_recorded_output(string ruleName, string caseName)
    {
        Assert.True(EmbeddedRules.TryGetValue(ruleName, out var rule), $"no embedded rule named '{ruleName}'");
        var exitCode = int.Parse(caseName[..caseName.IndexOf('-')]);
        var basePath = Path.Combine(FixtureRoot, ruleName, caseName);

        var output = new ArrayBufferWriter<byte>();
        Proxy.Apply(rule!, exitCode, File.ReadAllBytes(basePath + ".in.txt"), output, FixtureCwd);

        Assert.Equal(File.ReadAllText(basePath + ".out.txt"), Encoding.UTF8.GetString(output.WrittenSpan));
    }

    [Fact]
    public void Every_embedded_rule_has_a_fixture()
    {
        var missing = EmbeddedRules.Keys.Where(name =>
            !Directory.Exists(Path.Combine(FixtureRoot, name))
            || Directory.GetFiles(Path.Combine(FixtureRoot, name), "*.in.txt").Length == 0);
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_embedded_rule_is_valid()
    {
        foreach (var rule in EmbeddedRules.Values)
        {
            Steps.Build(rule.StepsFor(success: true), rule.Name, FixtureCwd);
            Steps.Build(rule.StepsFor(success: false), rule.Name, FixtureCwd);
        }
    }

    [Fact]
    public void Embedded_rule_names_are_unique()
    {
        var names = RuleStore.Embedded().Select(entry => entry.Rule.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
