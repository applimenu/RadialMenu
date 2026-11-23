using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RadialMenu1;

internal sealed class ScreenRecordingFrames : IDisposable
{
    private const uint CopyDesktop = 0x40cc0020; // SRCCOPY | CAPTUREBLT includes layered WPF windows.
    private const int Halftone = 4;
    private readonly Rectangle _source;
    private nint _screen, _canvas, _bitmap, _previousBitmap, _cursorHandle;
    private uint _cursorHotX, _cursorHotY;
    private int _cursorWidth, _cursorHeight;

    internal int Width { get; }
    internal int Height { get; }
    internal nint Pixels { get; }

    internal ScreenRecordingFrames(Rectangle source)
    {
        _source = source;
        // Preserve aspect ratio on portrait and wide displays while bounding H.264 work.
        double shrink = Math.Min(1, Math.Min(1920.0 / Math.Max(source.Width, source.Height),
            1080.0 / Math.Min(source.Width, source.Height)));
        Width = Math.Max(2, ((int)Math.Round(source.Width * shrink)) & ~1);
        Height = Math.Max(2, ((int)Math.Round(source.Height * shrink)) & ~1);

        try
        {
            _screen = GetDC(0);
            if (_screen == 0) throw new Win32Exception();
            _canvas = CreateCompatibleDC(_screen);
            if (_canvas == 0) throw new Win32Exception();
            var header = new BitmapHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = Width, Height = -Height, Planes = 1, BitCount = 32
            };
            _bitmap = CreateDIBSection(_screen, ref header, 0, out nint pixels, 0, 0);
            if (_bitmap == 0 || pixels == 0) throw new Win32Exception();
            Pixels = pixels;
            _previousBitmap = SelectObject(_canvas, _bitmap);
            if (_previousBitmap == 0 || _previousBitmap == -1) throw new Win32Exception();
            SetStretchBltMode(_canvas, Halftone);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static Rectangle MonitorAtCursor()
    {
        using var dpi = new PhysicalPixels();
        if (!GetCursorPos(out var cursor)) throw new Win32Exception();
        var monitor = MonitorFromPoint(cursor, 2); // nearest monitor for gaps between screens
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) throw new Win32Exception();
        return Rectangle.FromLTRB(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right, info.Bounds.Bottom);
    }

    internal void Capture()
    {
        bool copied = Width == _source.Width && Height == _source.Height
            ? BitBlt(_canvas, 0, 0, Width, Height, _screen, _source.Left, _source.Top, CopyDesktop)
            : StretchBlt(_canvas, 0, 0, Width, Height, _screen, _source.Left, _source.Top, _source.Width, _source.Height, CopyDesktop);
        if (!copied) throw new Win32Exception();
        DrawCursor();
        GdiFlush();
    }

    private void DrawCursor()
    {
        var cursor = new CursorInfo { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 ||
            !_source.Contains(cursor.Position.X, cursor.Position.Y)) return;
        if (_cursorHandle != cursor.Handle && !ReadCursorMetrics(cursor.Handle)) return;
        double sx = (double)Width / _source.Width;
        double sy = (double)Height / _source.Height;
        int x = (int)Math.Round((cursor.Position.X - _source.Left - (double)_cursorHotX) * sx);
        int y = (int)Math.Round((cursor.Position.Y - _source.Top - (double)_cursorHotY) * sy);
        int width = Math.Max(1, (int)Math.Round(_cursorWidth * sx));
        int height = Math.Max(1, (int)Math.Round(_cursorHeight * sy));
        DrawIconEx(_canvas, x, y, cursor.Handle, width, height, 0, 0, 3);
    }

    private bool ReadCursorMetrics(nint handle)
    {
        if (!GetIconInfo(handle, out var icon)) return false;
        try
        {
            nint image = icon.Color != 0 ? icon.Color : icon.Mask;
            int bytes = Marshal.SizeOf<BitmapInfo>();
            if (image == 0 || GetObject(image, bytes, out var size) != bytes || size.Width <= 0 || size.Height <= 0)
                return false;
            _cursorHandle = handle;
            _cursorHotX = icon.HotX;
            _cursorHotY = icon.HotY;
            _cursorWidth = size.Width;
            _cursorHeight = icon.Color != 0 ? size.Height : size.Height / 2;
            return true;
        }
        finally
        {
            if (icon.Mask != 0) DeleteObject(icon.Mask);
            if (icon.Color != 0) DeleteObject(icon.Color);
        }
    }

    public void Dispose()
    {
        if (_previousBitmap != 0 && _previousBitmap != -1) SelectObject(_canvas, _previousBitmap);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_canvas != 0) DeleteDC(_canvas);
        if (_screen != 0) ReleaseDC(0, _screen);
        _previousBitmap = _bitmap = _canvas = _screen = 0;
    }

    internal sealed class PhysicalPixels : IDisposable
    {
        private readonly nint _previous = SetThreadDpiAwarenessContext(-4); // PER_MONITOR_AWARE_V2
        public void Dispose()
        {
            if (_previous != 0) SetThreadDpiAwarenessContext(_previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public RectNative Bounds, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size, Flags; public nint Handle; public PointNative Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { public int IsIcon; public uint HotX, HotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPerPixel; public nint Bits;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(PointNative point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint data, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int GetObject(nint obj, int bytes, out BitmapInfo info);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint source, int sx, int sy, int sw, int sh, uint operation);
}
