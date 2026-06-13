using System.Runtime.InteropServices;

namespace Reclaim.App.Services;

/// <summary>
/// Wraps the Windows Shell Recycle Bin APIs: query how much is in it, and empty
/// it. Emptying is permanent, so the UI must confirm first.
/// </summary>
public static class RecycleBin
{
    /// <summary>Current Recycle Bin contents across all drives.</summary>
    public static (long Bytes, long Items) Query()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            // null root = all drives
            var hr = SHQueryRecycleBin(null, ref info);
            return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch
        {
            // Never let a Recycle Bin query failure break the caller (e.g. startup).
            return (0, 0);
        }
    }

    /// <summary>Permanently empties the Recycle Bin (all drives). Returns true on
    /// success. No confirmation UI here — the caller must confirm first.</summary>
    public static bool Empty()
    {
        // Flags: no confirmation dialog, no progress UI, no sound — Reclaim shows
        // its own confirmation before calling this.
        const uint SHERB_NOCONFIRMATION = 0x1;
        const uint SHERB_NOPROGRESSUI = 0x2;
        const uint SHERB_NOSOUND = 0x4;
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null,
            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // 0 = success; some systems return a non-zero code when already empty.
        return hr == 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
