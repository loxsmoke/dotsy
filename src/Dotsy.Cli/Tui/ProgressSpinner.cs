namespace Dotsy.Cli.Tui;

/// <summary>
/// Manages an animated progress spinner with rotating frames.
/// </summary>
internal sealed class ProgressSpinner : IDisposable
{
    private static readonly string[] Frames = ["◐", "◓", "◑", "◒"];

    private volatile bool isSpinning;
    private volatile int spinIndex;
    private readonly Timer spinnerTimer;
    private readonly Action<string> onSpinnerUpdate;

    /// <summary>
    /// Creates a new progress spinner.
    /// </summary>
    /// <param name="onUpdate">Callback invoked with each frame update (receives the current frame character).</param>
    public ProgressSpinner(Action<string> onUpdate)
    {
        onSpinnerUpdate = onUpdate ?? throw new ArgumentNullException(nameof(onUpdate));
        spinnerTimer = new Timer(SpinTick, null, System.Threading.Timeout.Infinite, 150);
    }

    /// <summary>
    /// Starts the spinner animation.
    /// </summary>
    public void Start()
    {
        if (isSpinning) return;

        isSpinning = true;
        spinIndex = 0;
        onSpinnerUpdate(Frames[0]);
        spinnerTimer.Change(150, 150);
    }

    /// <summary>
    /// Stops the spinner animation.
    /// </summary>
    public void Stop()
    {
        if (!isSpinning) return;

        isSpinning = false;
        spinnerTimer.Change(System.Threading.Timeout.Infinite, 0);
    }

    /// <summary>
    /// Gets whether the spinner is currently running.
    /// </summary>
    public bool IsSpinning => isSpinning;

    /// <summary>
    /// This method is called by timer.
    /// </summary>
    /// <param name="_"></param>
    private void SpinTick(object? _)
    {
        if (!isSpinning) return;

        spinIndex = (spinIndex + 1) % Frames.Length;
        var frame = Frames[spinIndex];
        onSpinnerUpdate(frame);
    }

    public void Dispose()
    {
        Stop();
        spinnerTimer.Dispose();
    }
}
