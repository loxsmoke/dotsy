using Dotsy.Cli.Tui.Colors;
using Dotsy.Core.Utils;

namespace Dotsy.Cli.Tui;

internal sealed class StatusBar : Label
{
    private string currentState = "idle";
    private string spinnerFrame = " ";
    private string sessionId    = "";
    private string modelId      = "dotsy";
    private float  ctxPct       = 0f;
    private int    runningPhase;

    public StatusBar()
    {
        X = 0; Y = 0;
        Width = Dim.Fill(); Height = 1;
        SetScheme(Palette.StatusScheme());
        UpdateText();
    }

    public void SetState(string state)    { currentState = state ?? "idle"; runningPhase = 0; UpdateText(); }
    public void SetSpinnerFrame(string f) { spinnerFrame = f ?? " "; runningPhase++; UpdateText(); }
    public void SetSession(string sid)    { sessionId    = sid; UpdateText(); }
    public void SetModel(string mid)      { modelId      = mid ?? "dotsy"; UpdateText(); }
    public void SetCtxPct(float pct)      { ctxPct       = pct; UpdateText(); }
    public void ApplyTheme()              { UpdateText(); } // re-read Palette schemes after a live re-theme
    public string State => currentState;

    private void UpdateText()
    {
        var sid = sessionId.Length > 0 ? $"{sessionId}  -  " : "";
        Text        = $"  dotsy  -  {sid}{modelId}  -  [{ctxPct:P0} ctx] {spinnerFrame} {currentState}";
        Text = Text.ToString()?.Replace(currentState, FormatState()) ?? "";
        SetScheme(ctxPct > 0.80f ? Palette.ErrStatusScheme()
                    : ctxPct > 0.50f ? Palette.WarnStatusScheme()
                    : Palette.StatusScheme());
        SetNeedsDraw();
    }

    private static readonly string[] AnimatedWords = ["thinking", "running", "compacting"];

    private string FormatState()
    {
        foreach (var word in AnimatedWords)
        {
            if (!currentState.StartsWithNoCase(word))
                continue;

            var phase = runningPhase % (word.Length * 2);
            bool growing = phase < word.Length;
            var upperCount = growing
                ? phase + 1
                : word.Length - (phase - word.Length + 1);
            var animated = new string(word.Select((ch, i) =>
                (growing ? i < upperCount : i >= word.Length - upperCount)
                    ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch)).ToArray());
            return animated + currentState[word.Length..];
        }

        return currentState;
    }
}
