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
            var info = new SHQUERYRBINFO();
            // The shell validates cbSize against the exact native struct size; with
            // 8-byte alignment (two __int64 members) that's 24 bytes on x64, not the
            // 20 you'd get from a packed layout. Marshal.SizeOf on the naturally-
            // aligned struct gives the right value.
            info.cbSize = Marshal.SizeOf<SHQUERYRBINFO>();
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
        // 0 (S_OK) = success. Some Windows versions return E_UNEXPECTED (0x8000FFFF)
        // when the bin was already empty — that's not a real failure for our purpose.
        const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
        return hr == 0 || hr == E_UNEXPECTED;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
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
