internal sealed record SourceLine(IReadOnlyList<Segment> Segments)
{
    public string Text { get; } = string.Concat(Segments.Select(s => s.Text));
}
