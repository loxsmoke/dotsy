using Terminal.Gui;

namespace Dotsy.Cli.Tui;

internal sealed class StatusBar : Label
{
    private string _state        = "idle";
    private string _spinnerFrame = " ";
    private string _sessionId    = "";
    private string _modelId      = "dotsy";
    private float  _ctxPct       = 0f;
    private int    _runningPhase;

    public StatusBar()
    {
        X = 0; Y = 0;
        Width = Dim.Fill(); Height = 1;
        ColorScheme = Palette.StatusScheme();
        UpdateText();
    }

    public void SetState(string state)    { _state        = state ?? "idle"; _runningPhase = 0; UpdateText(); }
    public void SetSpinnerFrame(string f) { _spinnerFrame = f     ?? " "; _runningPhase++; UpdateText(); }
    public void SetSession(string sid)    { _sessionId    = sid; UpdateText(); }
    public void SetModel(string mid)      { _modelId      = mid   ?? "dotsy"; UpdateText(); }
    public void SetCtxPct(float pct)      { _ctxPct       = pct;              UpdateText(); }
    public void ApplyTheme()              { UpdateText(); } // re-read Palette schemes after a live re-theme
    public string State => _state;

    private void UpdateText()
    {
        var sid = _sessionId.Length > 0 ? $"{_sessionId}  ·  " : "";
        Text        = $"  dotsy  ·  {sid}{_modelId}  ·  [{_ctxPct:P0} ctx] {_spinnerFrame} {_state}";
        Text = Text.ToString()?.Replace(_state, FormatState()) ?? "";
        ColorScheme = _ctxPct > 0.80f ? Palette.ErrStatusScheme()
                    : _ctxPct > 0.50f ? Palette.WarnStatusScheme()
                    : Palette.StatusScheme();
        SetNeedsDraw();
    }

    private static readonly string[] AnimatedWords = ["thinking", "running", "compacting"];

    private string FormatState()
    {
        foreach (var word in AnimatedWords)
        {
            if (!_state.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                continue;

            var phase = _runningPhase % (word.Length * 2);
            bool growing = phase < word.Length;
            var upperCount = growing
                ? phase + 1
                : word.Length - (phase - word.Length + 1);
            var animated = new string(word.Select((ch, i) =>
                (growing ? i < upperCount : i >= word.Length - upperCount)
                    ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch)).ToArray());
            return animated + _state[word.Length..];
        }

        return _state;
    }
}
