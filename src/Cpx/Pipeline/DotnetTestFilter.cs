using System.Buffers;
using System.Text.RegularExpressions;

namespace Cpx.Pipeline;

/// <summary>
/// Keeps failed tests (name, message, stack trace minus the reflection invoker frames), build errors and the run summary
/// from VSTest console output. The only step that needs state across lines.
/// If nothing is recognised the output is left untouched, so an unknown format is never hidden.
/// </summary>
sealed partial class DotnetTestFilter : IStep
{
    [GeneratedRegex(@"^\s*(Passed|Failed|Skipped)\s+\S")]
    private static partial Regex TestResult();

    static readonly SearchValues<string> Diagnostics =
        SearchValues.Create([": error ", "error MSB", "aborted", "Aborted", "Build FAILED", "Test Run "], StringComparison.Ordinal);

    // Only the reflection plumbing that invokes a test method. A frame like
    // System.Reflection.RuntimeModule.ResolveMember can be where the exception was thrown, so it stays.
    static readonly SearchValues<string> InvokerFrames =
        SearchValues.Create(
            [
                "at System.Reflection.MethodBaseInvoker.", "at System.Reflection.RuntimeMethodInfo.Invoke(",
                "at System.Reflection.MethodBase.Invoke(", "at System.RuntimeMethodHandle.InvokeMethod(", "at InvokeStub_",
            ],
            StringComparison.Ordinal);

    public bool Apply(LineBuffer buffer)
    {
        var kept = new List<ReadOnlyMemory<char>>();
        var inFailure = false;

        foreach (var line in buffer.Lines)
        {
            var span = line.Span;
            var trimmed = span.TrimStart();

            if (trimmed.StartsWith("Passed!") || trimmed.StartsWith("Failed!") || trimmed.StartsWith("Total tests:"))
            {
                inFailure = false;
                kept.Add(line);
            }
            else if (TestResult().IsMatch(span))
            {
                inFailure = trimmed.StartsWith("Failed");
                if (inFailure) kept.Add(line);
            }
            else if (inFailure)
            {
                if (!span.IsWhiteSpace() && !span.ContainsAny(InvokerFrames)) kept.Add(line);
            }
            else if (span.ContainsAny(Diagnostics))
            {
                kept.Add(line);
            }
        }

        if (kept.Count > 0)
        {
            buffer.Lines.Clear();
            buffer.Lines.AddRange(kept);
        }
        return true;
    }
}
