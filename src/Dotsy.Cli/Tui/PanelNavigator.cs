namespace Dotsy.Cli.Tui;

public readonly record struct FocusRingItem(View Scope, View Target)
{
    public FocusRingItem(View target)
        : this(target, target)
    {
    }

    public bool IsVisible => Scope.Visible && Target.Visible;
}

// Pure focus-ring logic for Tab / Shift+Tab panel switching, extracted from AgentWindow so the
// cycle order can be unit-tested without constructing any views.
public class PanelNavigator(params FocusRingItem[] ring)
{
    readonly List<FocusRingItem> items = [.. ring];

    public View Next(View? focused, bool back)
    {
        if (items.Count == 0)
            throw new ArgumentException("Focus ring must contain at least one item.");

        int i = IndexOf(focused);
        if (i < 0)
        {
            // unknown/hidden source: re-enter the ring at the start
            return items.FirstOrDefault(item => item.IsVisible).Target ??
                throw new ArgumentException("Focus ring must contain at least one visible item.");
        }

        int n = items.Count;
        for (int step = 1; step <= n; step++)
        {
            int next = back ? (i - step + n) % n : (i + step) % n;
            if (items[next].IsVisible)
                return items[next].Target;
        }

        throw new ArgumentException("Focus ring must contain at least one visible item.");
    }

    private int IndexOf(View? focused)
    {
        int bestIndex = -1;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].IsVisible)
                continue;

            int distance = DistanceToAncestor(focused, items[i].Scope);
            if (distance >= 0 && distance < bestDistance)
            {
                bestIndex = i;
                bestDistance = distance;
            }
        }

        return bestIndex;
    }

    private static int DistanceToAncestor(View? view, View ancestor)
    {
        for (int distance = 0; view is not null; distance++)
        {
            if (ReferenceEquals(view, ancestor))
                return distance;

            view = view.SuperView;
        }

        return -1;
    }
}
