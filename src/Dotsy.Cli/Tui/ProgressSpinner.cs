namespace Dotsy.Cli.Tui;

/// <summary>
/// Manages an animated progress spinner with rotating frames.
/// </summary>
internal sealed class ProgressSpinner : IDisposable
{
    private static readonly string[] Frames = ["◐", "◓", "◑", "◒"];

    private volatile int _spinIdx;
    private volatile bool _spinning;
    private readonly Timer _spinnerTimer;
    private readonly Action<string> _onFrameUpdate;

    /// <summary>
    /// Creates a new progress spinner.
    /// </summary>
    /// <param name="onFrameUpdate">Callback invoked with each frame update (receives the current frame character).</param>
    public ProgressSpinner(Action<string> onFrameUpdate)
    {
        _onFrameUpdate = onFrameUpdate ?? throw new ArgumentNullException(nameof(onFrameUpdate));
        _spinnerTimer = new Timer(SpinTick, null, System.Threading.Timeout.Infinite, 150);
    }

    /// <summary>
    /// Starts the spinner animation.
    /// </summary>
    public void Start()
    {
        if (_spinning) return;

        _spinning = true;
        _spinIdx = 0;
        _onFrameUpdate(Frames[0]);
        _spinnerTimer.Change(150, 150);
    }

    /// <summary>
    /// Stops the spinner animation.
    /// </summary>
    public void Stop()
    {
        if (!_spinning) return;

        _spinning = false;
        _spinnerTimer.Change(System.Threading.Timeout.Infinite, 0);
    }

    /// <summary>
    /// Gets whether the spinner is currently running.
    /// </summary>
    public bool IsSpinning => _spinning;

    private void SpinTick(object? _)
    {
        if (!_spinning) return;

        _spinIdx = (_spinIdx + 1) % Frames.Length;
        var frame = Frames[_spinIdx];
        _onFrameUpdate(frame);
    }

    public void Dispose()
    {
        Stop();
        _spinnerTimer.Dispose();
    }
}
