using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StickyTodo;

// Border that also clips its child to the inner rounded edge, so square content backgrounds
// (such as header bars) do not poke out of the corners of a transparent window.
public sealed class RoundedBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (Child is UIElement child)
        {
            double radius = Math.Max(0, CornerRadius.TopLeft - Math.Max(BorderThickness.Left, BorderThickness.Top));
            child.Clip = new RectangleGeometry(new Rect(child.RenderSize), radius, radius);
        }
        return size;
    }
}
