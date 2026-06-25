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

        approvalMsg = new Label { X = 2, Y = 0, Width = Dim.Fill(2), Text = "" };
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

    public Task<ApprovalChoice> ShowAsync(string toolName, string displayArg, bool allowForProject)
    {
        approvalTcs = new TaskCompletionSource<ApprovalChoice>();
        TuiSessionContext.App.Invoke(() =>
        {
            approvalMsg.Text = $"  {toolName}  {displayArg}";
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
}
