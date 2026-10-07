namespace Cpx.Pipeline;

/// <summary>
/// Command output as slices over one decoded buffer. Steps that only select lines
/// (keep, drop, dedupe, head, tail) move slices around and allocate no strings.
/// </summary>
sealed class LineBuffer
{
    public List<ReadOnlyMemory<char>> Lines { get; } = [];

    public static LineBuffer Parse(ReadOnlyMemory<char> text)
    {
        var buffer = new LineBuffer();
        var span = text.Span;
        var start = 0;
        while (start < span.Length)
        {
            var newline = span[start..].IndexOf('\n');
            var end = newline < 0 ? span.Length : start + newline;

            var line = span[start..end].TrimEnd();
            var lineStart = start;
            // Progress output rewrites the line with '\r'; only the last version was ever visible.
            var carriageReturn = line.LastIndexOf('\r');
            if (carriageReturn >= 0) lineStart += carriageReturn + 1;

            buffer.Lines.Add(text.Slice(lineStart, start + line.Length - lineStart));
            start = end + 1;
        }
        return buffer;
    }

    public void ReplaceAll(string message)
    {
        Lines.Clear();
        Lines.Add(message.AsMemory());
    }

    public void TrimBlankEdges()
    {
        var end = Lines.Count;
        while (end > 0 && Lines[end - 1].IsEmpty) end--;
        Lines.RemoveRange(end, Lines.Count - end);

        var start = 0;
        while (start < Lines.Count && Lines[start].IsEmpty) start++;
        Lines.RemoveRange(0, start);
    }
}
