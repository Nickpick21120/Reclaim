using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reclaim.App.Services;

/// <summary>
/// Returns the native Windows shell icon for a file or folder — the same icon
/// Explorer shows, including icons registered by the user's installed apps.
/// Icons are cached by extension (and one shared folder icon) so a 1,000-row
/// list makes at most a few dozen shell calls, not a thousand.
/// </summary>
public static class ShellIconProvider
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const string FolderKey = "<folder>";

    public static ImageSource? GetIcon(string fullPath, bool isDirectory)
    {
        var key = isDirectory ? FolderKey : NormalizeExtension(fullPath);
        return Cache.GetOrAdd(key, _ => LoadIcon(fullPath, isDirectory));
    }

    private static string NormalizeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? "<none>" : ext.ToLowerInvariant();
    }

    private static ImageSource? LoadIcon(string fullPath, bool isDirectory)
    {
        // USEFILEATTRIBUTES means Windows resolves the icon from the
        // extension/attributes alone, without touching the actual file —
        // so a path that no longer exists (or is slow to stat) still works.
        const uint SHGFI_ICON = 0x000000100;
        const uint SHGFI_SMALLICON = 0x000000001;
        const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        var attributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        // For files we only need the extension; pass a synthetic name so we never
        // depend on the real file being present.
        var lookupPath = isDirectory ? fullPath : "file" + NormalizeExtension(fullPath);

        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            lookupPath, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // cross-thread + immutable for caching
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
