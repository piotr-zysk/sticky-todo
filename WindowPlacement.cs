using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace StickyTodo;

public static class WindowPlacement
{
    const uint MonitorDefaultToNearest = 2;

    public static Point Place(Rect anchor, Size size, Rect workArea, IReadOnlyList<Rect> occupied, double gap = 4)
    {
        double x = Clamp(anchor.Left, workArea.Left, workArea.Right - size.Width);
        double maxTop = Math.Max(workArea.Top, workArea.Bottom - size.Height);
        var column = new Rect(x, workArea.Top, size.Width, workArea.Height);
        double below = anchor.Bottom, above = anchor.Top;
        foreach (var rect in occupied)
        {
            if (rect.Right <= column.Left || rect.Left >= column.Right) continue;
            below = Math.Max(below, rect.Bottom);
            above = Math.Min(above, rect.Top);
        }
        double top = below + gap;
        if (top + size.Height <= workArea.Bottom) return new Point(x, top);
        double stacked = above - gap - size.Height;
        if (stacked >= workArea.Top) return new Point(x, stacked);
        top = workArea.Bottom - anchor.Bottom >= anchor.Top - workArea.Top ? anchor.Bottom + gap : anchor.Top - gap - size.Height;
        return new Point(x, Clamp(top, workArea.Top, maxTop));
    }

    public static Rect WorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice;
        var dpi = VisualTreeHelper.GetDpi(window);
        double scaleX = transform?.M11 ?? dpi.DpiScaleX, scaleY = transform?.M22 ?? dpi.DpiScaleY;
        return new Rect(info.Work.Left * scaleX, info.Work.Top * scaleY, info.Work.Width * scaleX, info.Work.Height * scaleY);
    }

    static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), Math.Max(min, max));

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfo
    {
        public int Size;
        public PixelRect Monitor;
        public PixelRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
