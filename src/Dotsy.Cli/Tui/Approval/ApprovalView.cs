using Dotsy.Cli.Tui.Colors;

namespace Dotsy.Cli.Tui.Approval;

internal sealed class ApprovalView : FrameView
{
    private readonly Label approvalMsg;
    private readonly FlatButton btnOnce;
    private readonly FlatButton btnAlways;
    private readonly FlatButton btnDeny;
    private readonly FlatButton btnProject;
    private TaskCompletionSource<ApprovalChoice>? approvalTcs;

    public event EventHandler? ApprovalClosed;

    public ApprovalView()
    {
        Title = " Tool approval ";
        Visible = false;
        SetScheme(Palette.FocusedPanelScheme());

        // Height is pinned to the two rows above the button row so an over-long message can never
        // paint over the buttons, whatever a tool's approval formatter returns.
        approvalMsg = new Label { X = 2, Y = 0, Width = Dim.Fill(2), Height = 2, Text = "" };
        approvalMsg.SetScheme(Palette.Scheme());
        btnOnce = new FlatButton("Allow once") { X = 2, Y = 2 };
        btnAlways = new FlatButton("Always allow") { X = Pos.Right(btnOnce) + 2, Y = 2 };
        btnDeny = new FlatButton("Deny") { X = Pos.Right(btnAlways) + 2, Y = 2 };
        btnProject = new FlatButton("Allow for project") { X = 2, Y = 3, Visible = false };

        Add(approvalMsg, btnOnce, btnAlways, btnDeny, btnProject);
        PositionButtons();

        btnOnce.Fired += (_, _) => Accept(ApprovalChoice.AllowOnce);
        btnAlways.Fired += (_, _) => Accept(ApprovalChoice.AlwaysAllow);
        btnDeny.Fired += (_, _) => Accept(ApprovalChoice.Deny);
        btnProject.Fired += (_, _) => Accept(ApprovalChoice.AllowForProject);
    }

    // projectPath is null for an in-cwd write (button reads "Allow for project") or the cwd-relative
    // path to another project's root for an out-of-cwd write (e.g. "Allow for ..\some-folder").
    public Task<ApprovalChoice> ShowAsync(string toolName, string displayArg, bool allowForProject, string? projectPath = null)
    {
        approvalTcs = new TaskCompletionSource<ApprovalChoice>();
        TuiSessionContext.App.Invoke(() =>
        {
            approvalMsg.Text = $"{toolName}  {FitMessage(displayArg, Frame.Width)}";
            btnProject.SetLabel(projectPath is null ? "Allow for project" : $"Allow for {projectPath}");
            btnProject.Visible = allowForProject;
            PositionButtons();
            Visible = true;
            FocusFirstButton();
        });

        return approvalTcs.Task;
    }

    public void FocusFirstButton()
    {
        var first = ApprovalButtons().FirstOrDefault();
        if (first is not null) first.SetFocus();
        else SetFocus();
    }

    public void CycleFocus(bool back)
    {
        var buttons = ApprovalButtons();
        if (buttons.Count == 0)
        {
            SetFocus();
            return;
        }

        var focused = TuiSessionContext.App.Navigation?.GetFocused();
        int idx = buttons.FindIndex(b => b == focused);
        int next = idx < 0
            ? 0
            : ((idx + (back ? -1 : 1)) % buttons.Count + buttons.Count) % buttons.Count;
        buttons[next].SetFocus();
    }

    private void Accept(ApprovalChoice choice)
    {
        TuiSessionContext.App.Invoke(() =>
        {
            Visible = false;
            ApprovalClosed?.Invoke(this, EventArgs.Empty);
        });
        approvalTcs?.TrySetResult(choice);
    }

    private List<FlatButton> ApprovalButtons() =>
        SubViews.OfType<FlatButton>().Where(b => b.Visible).ToList();

    private void PositionButtons()
    {
        int availableWidth = Math.Max(1, Frame.Width - 4);
        int spacing = 2;

        var buttons = ApprovalButtons();
        if (buttons.Count == 0) return;

        int rowWidth = buttons.Sum(ButtonWidth) + spacing * (buttons.Count - 1);
        if (rowWidth > availableWidth)
        {
            int x = 2;
            int y = 2;
            foreach (var button in buttons)
            {
                int width = ButtonWidth(button);
                if (x > 2 && x + width > availableWidth + 2)
                {
                    x = 2;
                    y++;
                }

                button.X = x;
                button.Y = y;
                x += width + spacing;
            }
        }
        else
        {
            int x = 2;
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].X = x;
                buttons[i].Y = 2;
                x += ButtonWidth(buttons[i]) + spacing;
            }
        }
    }

    private static int ButtonWidth(View button) =>
        button.Text?.Length ?? Math.Max(1, button.Frame.Width);

    // Collapses the message to a single line and truncates it to the two rows the label owns.
    // Tool approval formatters are expected to return one-liners, but any tool relying on the
    // ITool default gets its raw input JSON here — newlines and all — and an unbounded message
    // used to wrap across the button row (seen with a Task call carrying a multi-paragraph
    // prompt). frameWidth may be 0 before the first layout; fall back to a conservative budget.
    internal static string FitMessage(string text, int frameWidth)
    {
        var oneLine = string.Join(' ', text.Split(
            ['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));
        int lineWidth = frameWidth > 8 ? frameWidth - 6 : 120;
        int budget = lineWidth * 2;
        return oneLine.Length <= budget ? oneLine : oneLine[..(budget - 1)] + "…";
    }
}
