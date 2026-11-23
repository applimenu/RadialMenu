using System.Runtime.InteropServices;

namespace RadialMenu1;

// Native Windows H.264 encoders accept NV12. Both capture dimensions are even.
internal sealed class ScreenVideoConverter(int width, int height)
{
    private readonly int[] _rgb = new int[checked(width * height)];
    private readonly byte[] _nv12 = new byte[checked(width * height * 3 / 2)];

    internal byte[] Convert(nint pixels)
    {
        Marshal.Copy(pixels, _rgb, 0, _rgb.Length);
        int chroma = width * height;
        for (int row = 0; row < height; row += 2)
        {
            int top = row * width;
            int bottom = top + width;
            int end = bottom;
            for (; top < end; top += 2, bottom += 2)
            {
                int a = _rgb[top], b = _rgb[top + 1], c = _rgb[bottom], d = _rgb[bottom + 1];
                _nv12[top] = Luma(a);
                _nv12[top + 1] = Luma(b);
                _nv12[bottom] = Luma(c);
                _nv12[bottom + 1] = Luma(d);

                // Average the two rows and columns that share this UV sample.
                int redBlue = (a & 0xff00ff) + (b & 0xff00ff) + (c & 0xff00ff) + (d & 0xff00ff);
                int red = redBlue >> 18;
                int blue = (redBlue & 0xffff) >> 2;
                int green = (((a >> 8) & 255) + ((b >> 8) & 255) + ((c >> 8) & 255) + ((d >> 8) & 255)) >> 2;
                _nv12[chroma++] = (byte)(128 + ((-26 * red - 87 * green + 112 * blue + 128) >> 8));
                _nv12[chroma++] = (byte)(128 + ((112 * red - 102 * green - 10 * blue + 128) >> 8));
            }
        }
        return _nv12;
    }

    private static byte Luma(int pixel)
    {
        int red = (pixel >> 16) & 255, green = (pixel >> 8) & 255, blue = pixel & 255;
        return (byte)(16 + ((47 * red + 157 * green + 16 * blue + 128) >> 8));
    }
}
