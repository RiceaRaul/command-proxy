using System.Text.Json.Serialization;

namespace Cpx.Rules;

sealed class RuleFile
{
    public List<Rule> Rules { get; set; } = [];
}

sealed class Rule
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Prefix matched against the arguments that do not start with '-'.</summary>
    public string[] Args { get; set; } = [];

    /// <summary>The rule does not match when any argument starts with one of these.</summary>
    public string[]? Unless { get; set; }

    /// <summary>Inserted right after the matched prefix.</summary>
    public string[]? AddArgs { get; set; }

    public bool MergeStderr { get; set; }

    public StepSpec[]? Steps { get; set; }
    public StepSpec[]? OnSuccess { get; set; }
    public StepSpec[]? OnFailure { get; set; }

    public StepSpec[] StepsFor(bool success) => (success ? OnSuccess : OnFailure) ?? Steps ?? [];
}

/// <summary>Flat on purpose: one shape for every step keeps deserialization trivial under AOT.</summary>
sealed class StepSpec
{
    public string Step { get; set; } = "";
    public string? Regex { get; set; }
    public string[]? Contains { get; set; }
    public string? With { get; set; }
    public int? N { get; set; }
    public bool Adjacent { get; set; }
    public string? Message { get; set; }
    public string? Name { get; set; }
}

sealed class HistoryEntry
{
    public DateTimeOffset Ts { get; set; }
    public string Cmd { get; set; } = "";
    public string Rule { get; set; } = "";
    public long In { get; set; }
    public long Out { get; set; }
    public long Ms { get; set; }
    public int Exit { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RuleFile))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(Dictionary<string, string>))]
sealed partial class CpxJson : JsonSerializerContext;

sealed class RuleException(string message) : Exception(message);
