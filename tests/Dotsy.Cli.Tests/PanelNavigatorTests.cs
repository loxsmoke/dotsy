using Dotsy.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace Dotsy.Cli.Tests;

[TestClass]
public sealed class PanelNavigatorTests
{
    [TestMethod]
    public void Next_Forward_ReturnsNextTargetInRing()
    {
        var ring = CreateRing(4);
        var navigator = new PanelNavigator(ring);

        Assert.AreSame(ring[1].Target, navigator.Next(ring[0].Target, back: false));
        Assert.AreSame(ring[2].Target, navigator.Next(ring[1].Target, back: false));
        Assert.AreSame(ring[3].Target, navigator.Next(ring[2].Target, back: false));
        Assert.AreSame(ring[0].Target, navigator.Next(ring[3].Target, back: false));
    }

    [TestMethod]
    public void Next_Backward_ReturnsPreviousTargetInRing()
    {
        var ring = CreateRing(4);
        var navigator = new PanelNavigator(ring);

        Assert.AreSame(ring[3].Target, navigator.Next(ring[0].Target, back: true));
        Assert.AreSame(ring[2].Target, navigator.Next(ring[3].Target, back: true));
        Assert.AreSame(ring[1].Target, navigator.Next(ring[2].Target, back: true));
        Assert.AreSame(ring[0].Target, navigator.Next(ring[1].Target, back: true));
    }

    [TestMethod]
    public void Next_FocusedDescendant_UsesOwningScope()
    {
        View conversation = new();
        View tools = new();
        View childInsideTools = new();
        tools.Add(childInsideTools);
        FocusRingItem[] ring = [new(conversation), new(tools)];
        var navigator = new PanelNavigator(ring);

        Assert.AreSame(conversation, navigator.Next(childInsideTools, back: false));
        Assert.AreSame(conversation, navigator.Next(childInsideTools, back: true));
    }

    [TestMethod]
    public void Next_NestedScopes_UsesClosestScope()
    {
        View tools = new();
        View filesFrame = new();
        View files = new();
        View input = new();
        filesFrame.Add(files);
        FocusRingItem[] ring = [new(tools), new(filesFrame, files), new(input)];
        var navigator = new PanelNavigator(ring);

        Assert.AreSame(input, navigator.Next(files, back: false));
        Assert.AreSame(tools, navigator.Next(files, back: true));
    }

    [TestMethod]
    public void Next_InvisibleFilesScope_IsSkipped()
    {
        View conversation = new();
        View tools = new();
        View filesFrame = new();
        View files = new();
        View input = new();
        filesFrame.Add(files);
        filesFrame.Visible = false;
        FocusRingItem[] ring = [new(conversation), new(tools), new(filesFrame, files), new(input)];
        var navigator = new PanelNavigator(ring);

        Assert.AreSame(input, navigator.Next(tools, back: false));
        Assert.AreSame(tools, navigator.Next(input, back: true));
        Assert.AreSame(conversation, navigator.Next(files, back: false));
        Assert.AreSame(conversation, navigator.Next(files, back: true));
    }

    [TestMethod]
    public void Next_UnknownReference_FallsBackToRingStart()
    {
        var ring = CreateRing(3);
        var navigator = new PanelNavigator(ring);

        Assert.AreSame(ring[0].Target, navigator.Next(new View(), back: false));
        Assert.AreSame(ring[0].Target, navigator.Next(new View(), back: true));
    }

    [TestMethod]
    public void Next_EmptyRing_Throws()
    {
        var navigator = new PanelNavigator();

        Assert.ThrowsExactly<ArgumentException>(() =>
            navigator.Next(new View(), back: false));
    }

    [TestMethod]
    public void Next_RingWithoutVisibleItems_Throws()
    {
        View hidden = new() { Visible = false };
        var navigator = new PanelNavigator(new FocusRingItem(hidden));

        Assert.ThrowsExactly<ArgumentException>(() =>
            navigator.Next(hidden, back: false));
    }

    private static FocusRingItem[] CreateRing(int count)
    {
        var ring = new FocusRingItem[count];
        for (int i = 0; i < ring.Length; i++)
            ring[i] = new FocusRingItem(new View());
        return ring;
    }
}
