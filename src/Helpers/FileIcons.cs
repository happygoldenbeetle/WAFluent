using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WhatsAppNative.Helpers;

/// <summary>
/// The icon Windows shows for a file type: the default app's (Photos for .png, VLC for .mp4
/// if that's yours, the zip folder for .zip), or the blank folded page when nothing opens it.
/// Read from the shell's jumbo image list, cached per extension.
/// </summary>
public static partial class FileIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0) ext = ".file-with-no-type";
        if (Cache.TryGetValue(ext, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            icon = Load(ext);
        }
        catch (Exception)
        {
            // Shell unavailable: the bubble keeps its emoji.
        }
        Cache[ext] = icon;
        return icon;
    }

    private static ImageSource? Load(string ext)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                          SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero)
            return null;

        // 256 px when the type has it; types that only ship 48 px sit small in the middle of
        // the jumbo tile, so those use the 48 px list instead.
        var pixels = IconPixels(SHIL_JUMBO, info.iIcon, out var size);
        if (pixels is null) return null;
        var bounds = OpaqueBounds(pixels, size);
        if (bounds.Width <= 64)
        {
            pixels = IconPixels(SHIL_EXTRALARGE, info.iIcon, out size) ?? pixels;
            bounds = OpaqueBounds(pixels, size);
        }
        return ToBitmap(pixels, size, bounds);
    }

    // ───────────── Pixels ─────────────

    private delegate int GetIconFn(IntPtr self, int index, int flags, out IntPtr icon);

    /// <summary>BGRA, premultiplied, top-down.</summary>
    private static byte[]? IconPixels(int list, int index, out int size)
    {
        size = 0;
        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");   // IImageList
        if (SHGetImageList(list, ref iid, out var imageList) != 0 || imageList == IntPtr.Zero) return null;
        try
        {
            // IImageList::GetIcon is vtable slot 3 (IUnknown) + 7.
            var vtable = Marshal.ReadIntPtr(imageList);
            var getIcon = Marshal.GetDelegateForFunctionPointer<GetIconFn>(Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size));
            if (getIcon(imageList, index, ILD_TRANSPARENT, out var icon) != 0 || icon == IntPtr.Zero) return null;
            try
            {
                return IconBitmap(icon, out size);
            }
            finally
            {
                DestroyIcon(icon);
            }
        }
        finally
        {
            Marshal.Release(imageList);
        }
    }

    private static byte[]? IconBitmap(IntPtr icon, out int size)
    {
        size = 0;
        if (!GetIconInfo(icon, out var ii)) return null;
        try
        {
            if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            size = bm.bmWidth;
            var height = bm.bmHeight;
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -height,   // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            var pixels = new byte[size * height * 4];
            var dc = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(dc, ii.hbmColor, 0, (uint)height, pixels, ref header, 0) == 0) return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
            // Old icons without alpha: everything opaque would be a black square; skip those.
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha) return null;
            for (var i = 0; i < pixels.Length; i += 4)   // straight -> premultiplied alpha
            {
                var a = pixels[i + 3];
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }
            return pixels;
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private readonly record struct Bounds(int X, int Y, int Width, int Height);

    private static Bounds OpaqueBounds(byte[] pixels, int size)
    {
        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (pixels[(y * size + x) * 4 + 3] > 8)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return maxX < 0 ? new Bounds(0, 0, size, size) : new Bounds(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>A square crop around the drawn part, so every icon fills its slot the same way.</summary>
    private static WriteableBitmap ToBitmap(byte[] pixels, int size, Bounds b)
    {
        var side = Math.Max(b.Width, b.Height);
        var ox = b.X - (side - b.Width) / 2;
        var oy = b.Y - (side - b.Height) / 2;
        var crop = new byte[side * side * 4];
        for (var y = 0; y < side; y++)
            for (var x = 0; x < side; x++)
            {
                int sx = ox + x, sy = oy + y;
                if (sx < 0 || sy < 0 || sx >= size || sy >= size) continue;
                Buffer.BlockCopy(pixels, (sy * size + sx) * 4, crop, (y * side + x) * 4, 4);
            }
        var bitmap = new WriteableBitmap(side, side);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(crop, 0, crop.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    // ───────────── Win32 ─────────────

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const int SHIL_EXTRALARGE = 2, SHIL_JUMBO = 4, ILD_TRANSPARENT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int list, ref Guid iid, out IntPtr imageList);

    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
