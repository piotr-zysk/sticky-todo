using System.Windows;
using StickyTodo;

static partial class Program
{
    static void CheckDetailsPlacement()
    {
        var area = new Rect(0, 0, 1000, 800);
        var anchor = new Rect(100, 100, 300, 200);
        var size = new Size(300, 150);
        var below = WindowPlacement.Place(anchor, size, area, Array.Empty<Rect>());
        Check(below == new Point(100, 304), $"details dock below the main window with a 4 DIP gap ({below})");

        var low = WindowPlacement.Place(new Rect(100, 600, 300, 150), size, area, Array.Empty<Rect>());
        Check(low == new Point(100, 446), $"details flip above near the bottom of the work area ({low})");

        var stacked = WindowPlacement.Place(anchor, size, area, new[] { new Rect(below, size) });
        Check(stacked == new Point(100, 458), $"a second details window stacks below the first ({stacked})");

        var stackedAbove = WindowPlacement.Place(new Rect(100, 600, 300, 150), size, area, new[] { new Rect(100, 446, 300, 150) });
        Check(stackedAbove == new Point(100, 292), $"a second details window stacks above the first ({stackedAbove})");

        var clamped = WindowPlacement.Place(new Rect(900, 100, 300, 200), size, area, Array.Empty<Rect>());
        Check(clamped == new Point(700, 304), $"details clamp horizontally into the work area ({clamped})");

        var taller = new Size(300, 500);
        var belowSide = WindowPlacement.Place(new Rect(100, 100, 300, 300), taller, area, Array.Empty<Rect>());
        Check(belowSide == new Point(100, 300), $"details use the larger below side when neither fits ({belowSide})");

        var aboveSide = WindowPlacement.Place(new Rect(100, 300, 300, 300), taller, area, Array.Empty<Rect>());
        Check(aboveSide == new Point(100, 0), $"details use the larger above side when neither fits ({aboveSide})");

        foreach (var point in new[] { belowSide, aboveSide })
            Check(point.Y >= area.Top && point.Y + taller.Height <= area.Bottom, "clamped details stay inside the work area");
    }
}
