using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace RadialMenu1;

internal static class GlassBackdrop
{
    private const int Size = 200;
    private static readonly Lazy<(int[] Offsets, byte[] Lighting)> Lens = new(BuildLens);
    internal static readonly Lazy<BitmapSource> Preview = new(CreatePreview);

    // The small image lives only in memory.
    public static BitmapSource? Capture(int cursorX, int cursorY, int span)
    {
        if (span < 2) return null;
        try
        {
            var sourceBounds = new Rectangle(cursorX - span / 2, cursorY - span / 2, span, span);
            var area = Rectangle.Intersect(sourceBounds, System.Windows.Forms.SystemInformation.VirtualScreen);
            if (area.IsEmpty) return null;

            using var capture = new Bitmap(span, span, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(capture))
            {
                graphics.Clear(System.Drawing.Color.FromArgb(25, 39, 55));
                graphics.CopyFromScreen(area.X, area.Y, area.X - sourceBounds.X, area.Y - sourceBounds.Y, area.Size);
            }

            using var small = new Bitmap(Size, Size, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(capture, 0, 0, Size, Size);
            }

            int stride = Size * 4;
            var source = new byte[Size * stride];
            var bits = small.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < Size; row++)
                    Marshal.Copy(bits.Scan0 + row * bits.Stride, source, row * stride, stride);
            }
            finally { small.UnlockBits(bits); }

            return Warp(source);
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
            return null;
        }
    }

    private static BitmapSource CreatePreview()
    {
        var samples = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double beam = Math.Exp(-Math.Pow((x - y * 0.64 - 34) / 28, 2));
                bool band = (x + 2 * y) % 69 < 8;
                int index = (y * Size + x) * 4;
                samples[index] = (byte)(38 + 115 * beam + (band ? 26 : 0));
                samples[index + 1] = (byte)(31 + 91 * beam + (band ? 28 : 0) + y / 8);
                samples[index + 2] = (byte)(24 + 61 * beam + (band ? 36 : 0) + x / 7);
            }
        }
        return Warp(samples);
    }

    private static BitmapSource Warp(byte[] source)
    {
        var (offsets, lighting) = Lens.Value;
        var output = new byte[source.Length];
        for (int pixel = 0; pixel < lighting.Length; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int src = offsets[pixel * 3 + channel];
                int softened = (source[src] * 4 + source[src - 8] + source[src + 8] +
                                source[src - Size * 8] + source[src + Size * 8]) / 8;
                output[pixel * 4 + channel] = (byte)Math.Min(255, softened + lighting[pixel]);
            }
        }
        var image = BitmapSource.Create(Size, Size, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null, output, Size * 4);
        image.Freeze();
        return image;
    }

    private static (int[] Offsets, byte[] Lighting) BuildLens()
    {
        const double radius = Size / 2.0;
        var offsets = new int[Size * Size * 3];
        var lighting = new byte[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - radius, dy = y + 0.5 - radius;
                double r = Math.Sqrt(dx * dx + dy * dy);
                double edge = Math.Clamp((r / radius - 0.73) / 0.27, 0, 1);
                double lens = 1 - 0.34 * edge * edge;
                int index = y * Size + x;
                lighting[index] = (byte)(28 * edge * edge);
                for (int channel = 0; channel < 3; channel++)
                {
                    double chroma = 1 + (channel - 1) * 0.036 * edge * edge;
                    int sx = Math.Clamp((int)(radius + dx * lens * chroma), 2, Size - 3);
                    int sy = Math.Clamp((int)(radius + dy * lens * chroma), 2, Size - 3);
                    offsets[index * 3 + channel] = (sy * Size + sx) * 4 + channel;
                }
            }
        }
        return (offsets, lighting);
    }
}
