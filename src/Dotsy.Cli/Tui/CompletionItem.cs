namespace Dotsy.Cli.Tui;

internal sealed record CompletionItem(string Display, string Replacement)
{
    public override string ToString() => "  " + Display;
}
