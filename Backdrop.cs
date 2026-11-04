using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Odak;

/// <summary>
/// Kendi Liquid Glass zeminimiz. Pencere açılmadan hemen önce arkasındaki ekran bölgesi yakalanır,
/// bulanıklaştırılır ve canlılaştırılır (doygunluk artırılır). Ardından camın kenarlarında Apple'ın
/// Liquid Glass'ındaki gibi mercek kırılması uygulanır: kenara yaklaştıkça arka plan içe doğru bükülür.
/// Windows'un "Saydamlık efektleri" kapalı olsa bile çalışır.
/// </summary>
internal sealed class Backdrop
{
    private const int Downscale = 3;

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

    /// <summary>Ekrandaki piksel dikdörtgenini yakalar; bulanık ve canlı temel görüntüyü hazırlar.</summary>
    public static Backdrop? Capture(int x, int y, int width, int height, double dpiScale)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            using var full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

            int sw = Math.Max(8, width / Downscale), sh = Math.Max(8, height / Downscale);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, sw, sh);
            }

            var data = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var pixels = new int[sw * sh];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            small.UnlockBits(data);

            // Üç geçişli kutu bulanıklığı ≈ Gauss (arka plan seçilir ama yazıyı rahatsız etmez).
            var tmp = new int[pixels.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BlurLine(pixels, tmp, sw, sh, 4, horizontal: true);
                BlurLine(tmp, pixels, sw, sh, 4, horizontal: false);
            }
            Vibrance(pixels, 1.45);
            return new Backdrop(pixels, sw, sh, dpiScale);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Verilen cam boyutu (DIP) için kenar kırılmalı görüntüyü üretir. Görüntü WPF'te tam olarak
    /// camın boyutunda görünecek DPI ile döner.
    /// </summary>
    public BitmapSource Render(double widthDip, double heightDip, double radiusDip)
    {
        double k = _scale / Downscale; // DIP → küçük görüntü pikseli
        int w = Math.Clamp((int)Math.Round(widthDip * k), 1, _w);
        int h = Math.Clamp((int)Math.Round(heightDip * k), 1, _h);
        double r = radiusDip * k;
        double band = 26 * k;       // kırılmanın etkili olduğu kenar şeridi
        double shift = 16 * k;      // en dış kenardaki en büyük bükülme
        double cx = w / 2.0, cy = h / 2.0, hx = w / 2.0 - r, hy = h / 2.0 - r;

        var output = new int[w * h];
        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                // Yuvarlatılmış dikdörtgenin işaretli uzaklık alanı ve dış normal.
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

                double inside = -dist; // kenardan içeri uzaklık
                double sx = px, sy = py;
                if (inside < band)
                {
                    double t = 1 - Math.Max(0, inside) / band;
                    double amount = shift * t * t * t; // kenara doğru hızla artan mercek bükülmesi
                    sx -= nx * amount;
                    sy -= ny * amount;
                }
                output[py * w + px] = Sample(sx, sy, inside < band ? (1 - Math.Max(0, inside) / band) : 0);
            }
        }

        double dpi = 96.0 * _scale / Downscale;
        var image = BitmapSource.Create(w, h, dpi, dpi, System.Windows.Media.PixelFormats.Bgra32, null, output, w * 4);
        image.Freeze();
        return image;
    }

    /// <summary>Çift doğrusal örnekleme; kenara yakın yerde ışığı hafifçe artırır (camın kalınlaşan kenarı).</summary>
    private int Sample(double x, double y, double edge)
    {
        x = Math.Clamp(x, 0, _w - 1.001);
        y = Math.Clamp(y, 0, _h - 1.001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int c00 = _pixels[y0 * _w + x0], c10 = _pixels[y0 * _w + x0 + 1];
        int c01 = _pixels[(y0 + 1) * _w + x0], c11 = _pixels[(y0 + 1) * _w + x0 + 1];
        double lift = 1 + 0.10 * edge * edge;
        int Ch(int shiftBits)
        {
            double a = ((c00 >> shiftBits) & 255) * (1 - fx) + ((c10 >> shiftBits) & 255) * fx;
            double b = ((c01 >> shiftBits) & 255) * (1 - fx) + ((c11 >> shiftBits) & 255) * fx;
            return Math.Min(255, (int)((a * (1 - fy) + b * fy) * lift));
        }
        return unchecked((int)0xFF000000) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    /// <summary>Apple'ın "vibrancy" etkisi gibi renkleri canlandırır.</summary>
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
