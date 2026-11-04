using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Odak;

/// <summary>Windows API: global hotkey and shell icons.</summary>
internal static class Native
{
    // ---- Global hotkey
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- Shell icons (the real icons of Start menu apps)
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);

    private const int SIIGBF_ICONONLY = 0x4;

    // Shell icons work reliably on an STA thread; a single background STA queue is used.
    private static readonly System.Collections.Concurrent.BlockingCollection<(string path, int px, System.Threading.Tasks.TaskCompletionSource<BitmapSource?> tcs)> IconQueue = StartIconThread();

    private static System.Collections.Concurrent.BlockingCollection<(string, int, System.Threading.Tasks.TaskCompletionSource<BitmapSource?>)> StartIconThread()
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<(string, int, System.Threading.Tasks.TaskCompletionSource<BitmapSource?>)>();
        var thread = new System.Threading.Thread(() =>
        {
            foreach (var (path, px, tcs) in queue.GetConsumingEnumerable())
            {
                try { tcs.TrySetResult(ShellIcon(path, px)); }
                catch { tcs.TrySetResult(null); }
            }
        })
        { IsBackground = true, Name = "Spotlight simgeleri" };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return queue;
    }

    /// <summary>Loads the icon on the background STA thread.</summary>
    public static System.Threading.Tasks.Task<BitmapSource?> ShellIconAsync(string parsingName, int px)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<BitmapSource?>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        IconQueue.Add((parsingName, px, tcs));
        return tcs.Task;
    }

    /// <summary>Returns the shell item's icon with transparency preserved (null = not found).</summary>
    public static BitmapSource? ShellIcon(string parsingName, int px)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_ICONONLY, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp);
                if (bmp.bmBits == IntPtr.Zero || bmp.bmBitsPixel != 32)
                {
                    var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    fallback.Freeze();
                    return fallback;
                }
                int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight), stride = bmp.bmWidthBytes;
                var buffer = new byte[stride * h];
                Marshal.Copy(bmp.bmBits, buffer, 0, buffer.Length);
                if (bmp.bmHeight > 0)
                {
                    // Bottom-up DIB: flip the rows.
                    var flipped = new byte[buffer.Length];
                    for (int y = 0; y < h; y++) Buffer.BlockCopy(buffer, y * stride, flipped, (h - 1 - y) * stride, stride);
                    buffer = flipped;
                }
                var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buffer, stride);
                image.Freeze();
                return image;
            }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
