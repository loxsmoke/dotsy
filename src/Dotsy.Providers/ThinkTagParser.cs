using System.Text;

namespace Dotsy.Providers;

// Splits a streamed content string into plain-text and <think>…</think> reasoning
// segments, tolerating tags that straddle chunk boundaries (a trailing partial tag is
// held back until the next chunk completes or disproves it).
internal sealed class ThinkTagParser
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";
    private readonly StringBuilder _buffer = new();
    private bool _inThink;

    public readonly record struct Segment(bool IsThinking, string Text);

    public IEnumerable<Segment> Process(string text)
    {
        _buffer.Append(text);

        while (_buffer.Length > 0)
        {
            var current = _buffer.ToString();
            var (tag, isThinking) = _inThink ? (CloseTag, true) : (OpenTag, false);
            var at = current.IndexOf(tag, StringComparison.Ordinal);

            if (at < 0)
            {
                // No complete tag. Emit everything except a possible partial tag at the end.
                var keep = LongestSuffixThatStarts(tag, current);
                var emit = current.Length - keep;
                if (emit > 0)
                {
                    yield return new Segment(isThinking, current[..emit]);
                    _buffer.Remove(0, emit);
                }
                yield break;
            }

            if (at > 0)
                yield return new Segment(isThinking, current[..at]);
            _buffer.Remove(0, at + tag.Length);
            _inThink = !_inThink;
        }
    }

    public IEnumerable<Segment> Complete()
    {
        if (_buffer.Length == 0)
            yield break;
        var text = _buffer.ToString();
        _buffer.Clear();
        yield return new Segment(_inThink, text);
    }

    private static int LongestSuffixThatStarts(string prefix, string text)
    {
        var max = Math.Min(prefix.Length - 1, text.Length);
        for (var len = max; len > 0; len--)
        {
            if (text.AsSpan(text.Length - len, len).SequenceEqual(prefix.AsSpan(0, len)))
                return len;
        }
        return 0;
    }
}
