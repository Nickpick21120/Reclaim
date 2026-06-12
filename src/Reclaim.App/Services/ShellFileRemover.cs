using System.IO;
using System.Runtime.InteropServices;
using Reclaim.Core.Cleanup;

namespace Reclaim.App.Services;

/// <summary>
/// Removes files/folders via the Windows Shell, so Recycle Bin deletion behaves
/// exactly like Explorer's (with undo). This is the ONLY component in the app
/// that performs destructive file operations; everything upstream merely plans.
/// </summary>
public sealed class ShellFileRemover : IFileRemover
{
    public RemovalOutcome Remove(string path, DeletionMode mode)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return RemovalOutcome.Removed; // already gone — treat as success

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Double-null-terminated list of source paths.
            pFrom = path + "\0\0",
            fFlags = FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };

        if (mode == DeletionMode.RecycleBin)
            op.fFlags |= FOF_ALLOWUNDO; // route to Recycle Bin instead of permanent

        var rc = SHFileOperation(ref op);
        if (rc == 0 && !op.fAnyOperationsAborted)
            return RemovalOutcome.Removed;

        // Map known "file in use / sharing violation" codes so the UI can report
        // them distinctly from generic errors.
        // 0x20 = DE_ACCESSDENIEDSRC / sharing, 0x7C/0x7E relate to locked paths,
        // 32 = ERROR_SHARING_VIOLATION.
        return rc is 0x20 or 32 or 0x7C or 0x7E
            ? RemovalOutcome.InUse
            : RemovalOutcome.Error;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
