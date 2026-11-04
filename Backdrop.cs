using System;
using System.Windows.Media.Imaging;


namespace Odak;

/// <summary>
/// Our own clear Liquid Glass. The screen region behind the window is captured (and refreshed live while
/// Spotlight is open), only lightly softened so whatever is behind stays clearly visible, then the glass
/// edges bend the light like a real lens: the closer to the rim, the stronger the refraction, with a hint
/// of chromatic dispersion. Works even when Windows "Transparency effects" are turned off.
/// </summary>
internal sealed class Backdrop
{
    private const int Downscale = 2;

    private readonly int[] _pixels;
    private readonly int _w, _h;
    private readonly double _scale;

    private Backdrop(int[] pixels, int w, int h, double scale)
    {
        _pixels = pixels;
        _w = w;
        _h = h;
        _scale = scale;
    }

    /// <summary>Captures a pixel rectangle of the screen and prepares the lightly softened base image.</summary>
    public static Backdrop? Capture(int x, int y, int width, int height, double dpiScale)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            // One GDI StretchBlt straight into a half-size buffer: a few milliseconds, so the live refresh
            // only needs to hide the window from capture very briefly.
            int sw = Math.Max(8, width / Downscale), sh = Math.Max(8, height / Downscale);
            var pixels = Native.CaptureScaled(x, y, width, height, sw, sh);
            if (pixels == null) return null;

            // A light two-pass softening: the background stays clearly readable, like clear glass, but
            // fine detail no longer fights with the text on top.
            var tmp = new int[pixels.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                BlurLine(pixels, tmp, sw, sh, 1, horizontal: true);
                BlurLine(tmp, pixels, sw, sh, 1, horizontal: false);
            }
            Vibrance(pixels, 1.18);
            return new Backdrop(pixels, sw, sh, dpiScale);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Produces the edge-refracted image for the given glass size (DIP). The image is returned with a DPI
    /// that makes it appear in WPF at exactly the size of the glass.
    /// </summary>
    public BitmapSource Render(double widthDip, double heightDip, double radiusDip)
    {
        double k = _scale / Downscale; // DIP → small-image pixels
        int w = Math.Clamp((int)Math.Round(widthDip * k), 1, _w);
        int h = Math.Clamp((int)Math.Round(heightDip * k), 1, _h);
        double r = radiusDip * k;
        double band = 30 * k;       // width of the refracting rim
        double shift = 20 * k;      // strongest bend, at the very edge
        double cx = w / 2.0, cy = h / 2.0, hx = w / 2.0 - r, hy = h / 2.0 - r;

        var output = new int[w * h];
        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                // Signed distance to the rounded rectangle, and its outward normal.
                double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
                double qx = Math.Abs(dx) - hx, qy = Math.Abs(dy) - hy;
                double nx, ny, dist;
                if (qx > 0 && qy > 0)
                {
                    double len = Math.Sqrt(qx * qx + qy * qy);
                    dist = len - r;
                    nx = qx / len * Math.Sign(dx);
                    ny = qy / len * Math.Sign(dy);
                }
                else if (qx > qy)
                {
                    dist = qx - r;
                    nx = Math.Sign(dx); ny = 0;
                }
                else
                {
                    dist = qy - r;
                    nx = 0; ny = Math.Sign(dy);
                }

                double inside = -dist; // distance in from the rim
                if (inside >= band)
                {
                    // The clear centre: background as is.
                    output[py * w + px] = _pixels[Math.Min(py, _h - 1) * _w + Math.Min(px, _w - 1)];
                    continue;
                }

                // Lens rim: background is pulled inward, red bends a little more than blue (dispersion).
                double t = 1 - Math.Max(0, inside) / band;
                double amount = shift * t * t * t;
                double lift = 1 + 0.05 * t * t; // the thicker edge catches a little more light
                int cr = Channel(px - nx * amount * 1.12, py - ny * amount * 1.12, 16, lift);
                int cg = Channel(px - nx * amount, py - ny * amount, 8, lift);
                int cb = Channel(px - nx * amount * 0.88, py - ny * amount * 0.88, 0, lift);
                output[py * w + px] = unchecked((int)0xFF000000) | (cr << 16) | (cg << 8) | cb;
            }
        }

        double dpi = 96.0 * _scale / Downscale;
        var image = BitmapSource.Create(w, h, dpi, dpi, System.Windows.Media.PixelFormats.Bgra32, null, output, w * 4);
        image.Freeze();
        return image;
    }

    /// <summary>Bilinear sample of one colour channel, brightened by <paramref name="lift"/>.</summary>
    private int Channel(double x, double y, int shiftBits, double lift)
    {
        x = Math.Clamp(x, 0, _w - 1.001);
        y = Math.Clamp(y, 0, _h - 1.001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int c00 = _pixels[y0 * _w + x0], c10 = _pixels[y0 * _w + x0 + 1];
        int c01 = _pixels[(y0 + 1) * _w + x0], c11 = _pixels[(y0 + 1) * _w + x0 + 1];
        double a = ((c00 >> shiftBits) & 255) * (1 - fx) + ((c10 >> shiftBits) & 255) * fx;
        double b = ((c01 >> shiftBits) & 255) * (1 - fx) + ((c11 >> shiftBits) & 255) * fx;
        return Math.Min(255, (int)((a * (1 - fy) + b * fy) * lift));
    }

    /// <summary>Makes colours livelier, like Apple's "vibrancy".</summary>
    private static void Vibrance(int[] px, double saturation)
    {
        for (int i = 0; i < px.Length; i++)
        {
            int c = px[i];
            double r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
            double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            int R = (int)Math.Clamp(l + (r - l) * saturation, 0, 255);
            int G = (int)Math.Clamp(l + (g - l) * saturation, 0, 255);
            int B = (int)Math.Clamp(l + (b - l) * saturation, 0, 255);
            px[i] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
        }
    }

    private static void BlurLine(int[] src, int[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w;
        int len = horizontal ? w : h;
        int div = 2 * r + 1;
        for (int line = 0; line < lines; line++)
        {
            int At(int i)
            {
                int k = Math.Clamp(i, 0, len - 1);
                return horizontal ? src[line * w + k] : src[k * w + line];
            }

            int rs = 0, gs = 0, bs = 0;
            for (int i = -r; i <= r; i++)
            {
                int c = At(i);
                rs += (c >> 16) & 255; gs += (c >> 8) & 255; bs += c & 255;
            }
            for (int i = 0; i < len; i++)
            {
                int idx = horizontal ? line * w + i : i * w + line;
                dst[idx] = unchecked((int)0xFF000000) | ((rs / div) << 16) | ((gs / div) << 8) | (bs / div);
                int o = At(i - r), n = At(i + r + 1);
                rs += ((n >> 16) & 255) - ((o >> 16) & 255);
                gs += ((n >> 8) & 255) - ((o >> 8) & 255);
                bs += (n & 255) - (o & 255);
            }
        }
    }
}
