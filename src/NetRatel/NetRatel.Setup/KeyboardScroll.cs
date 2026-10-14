using Termina.Layout;

namespace NetRatel.Setup;

/// <summary>Keeps Termina controls visible when a form is taller than the terminal.</summary>
internal sealed class KeyboardScroll(ScrollableContainerNode scroll, VerticalLayout form, int width)
{
    public int Offset => scroll.ScrollOffset;
    public int ViewportHeight => Math.Max(1, scroll.ContentHeight - scroll.MaxScroll);
    public void Page(bool down) { if (down) ((IScrollable)scroll).ScrollDown(Math.Max(1, ViewportHeight - 1)); else ((IScrollable)scroll).ScrollUp(Math.Max(1, ViewportHeight - 1)); }
    public void Home(bool end) => scroll.ScrollTo(end ? scroll.MaxScroll : 0);
    public void Restore(int offset) => scroll.ScrollTo(offset);
    public void EnsureVisible(IFocusable? focus)
    {
        if (focus is null || !Find(form, focus, 0, out var top, out var height)) return;
        if (top < scroll.ScrollOffset) scroll.ScrollTo(Math.Max(0, top - 1));
        else if (top + height > scroll.ScrollOffset + ViewportHeight) scroll.ScrollTo(top + height - ViewportHeight);
    }
    private bool Find(ILayoutNode node, IFocusable wanted, int top, out int targetTop, out int targetHeight)
    {
        var measured = node.Measure(new Size(Math.Max(1, width), int.MaxValue / 2));
        var height = node.HeightConstraint.Compute(int.MaxValue / 2, measured.Height, 0);
        if (ReferenceEquals(node, wanted)) { targetTop = top; targetHeight = Math.Max(1, height); return true; }
        if (node is VerticalLayout vertical)
        {
            var y = top;
            foreach (var child in vertical.Children)
            {
                if (Find(child, wanted, y, out targetTop, out targetHeight)) return true;
                var childSize = child.Measure(new Size(Math.Max(1, width), int.MaxValue / 2));
                y += child.HeightConstraint.Compute(int.MaxValue / 2, childSize.Height, 0) + vertical.Spacing;
            }
        }
        targetTop = targetHeight = 0; return false;
    }
}
