using Reclaim.Core.Formatting;
using Reclaim.Core.Scanning;

namespace Reclaim.Core.Knowledge;

/// <summary>
/// A catalog of common files, folders, and extensions with plain-language
/// descriptions, and a resolver that picks the best match for a given node.
/// Matching precedence: exact filename > folder name > extension > generic
/// fallback, so the most specific description always wins.
/// </summary>
public static class FileKnowledgeBase
{
    public static FileInfoResult Describe(FileSystemNode node)
    {
        var name = node.Name;

        // 1) Exact filename match (most specific).
        var byName = All.FirstOrDefault(k =>
            k.MatchKind == KnowledgeMatch.ExactName &&
            string.Equals(k.Token, name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return ToResult(byName, known: true);

        // 2) Folder-name match (only for directories).
        if (node.IsDirectory)
        {
            var byFolder = All.FirstOrDefault(k =>
                k.MatchKind == KnowledgeMatch.FolderName &&
                string.Equals(k.Token, name, StringComparison.OrdinalIgnoreCase));
            if (byFolder is not null)
                return ToResult(byFolder, known: true);
        }

        // 3) Extension match (only for files).
        if (!node.IsDirectory)
        {
            var ext = GetExtension(name);
            if (ext.Length > 0)
            {
                var byExt = All.FirstOrDefault(k =>
                    k.MatchKind == KnowledgeMatch.Extension &&
                    string.Equals(k.Token, ext, StringComparison.OrdinalIgnoreCase));
                if (byExt is not null)
                    return ToResult(byExt, known: true);
            }
        }

        // 4) Location-aware context: even without a specific entry, the path
        // tells us a lot (a file in System32 is a system file; one in a user's
        // Documents is personal data). This is "informed" rather than "known".
        var contextual = FromLocation(node);
        if (contextual is not null)
            return contextual;

        // 5) Generic fallback.
        return Generic(node);
    }

    /// <summary>
    /// Infers context from where a file lives and its extension, so the panel
    /// always shows something more useful than "no info". Returns null only if
    /// nothing meaningful can be said (handled by the generic fallback).
    /// </summary>
    private static FileInfoResult? FromLocation(FileSystemNode node)
    {
        var path = node.FullPath.Replace('/', '\\');
        var lower = path.ToLowerInvariant();
        var ext = node.IsDirectory ? "" : GetExtension(node.Name);

        // Helper to build a contextual (informed-but-not-exact) result.
        FileInfoResult Ctx(string title, string desc, string by, string size,
                           RemovalSafety safety, string note) => new()
        {
            Title = title, Description = desc, CreatedBy = by, TypicalSize = size,
            Safety = safety, SafetyNote = note, IsKnown = false,
        };

        var extLabel = ext.Length > 0 ? $"{ext} " : "";

        // --- Windows system locations ---
        if (lower.Contains(@"\windows\system32") || lower.Contains(@"\windows\syswow64"))
        {
            var what = ext switch
            {
                ".dll" => "a Windows system library (DLL) that programs and the OS load to share code",
                ".exe" => "a Windows system program",
                ".sys" => "a Windows kernel driver or system module",
                ".drv" => "a device driver used by Windows",
                _ => "a Windows operating-system file",
            };
            return Ctx(
                "Windows system file",
                $"This is {what}. It lives in the core Windows system folder.",
                "Windows / Microsoft",
                node.IsDirectory ? ByteSize.Format(node.SizeBytes) : "KB to a few MB",
                RemovalSafety.SystemManaged,
                "Part of Windows — don't delete. Removing system files can break your PC.");
        }

        if (lower.Contains(@"\windows\winsxs"))
            return Ctx("Windows component store",
                "Part of WinSxS, where Windows keeps component and update files needed for repairs and rollbacks.",
                "Windows servicing", "Very large overall (many GB)",
                RemovalSafety.SystemManaged,
                "Never delete by hand. Reclaim space with 'DISM /Online /Cleanup-Image /StartComponentCleanup'.");

        if (lower.Contains(@"\windows\")) 
            return Ctx(
                node.IsDirectory ? "Windows folder" : $"Windows {extLabel}file",
                "This lives inside the Windows directory and is most likely part of the operating system.",
                "Windows / Microsoft",
                ByteSize.Format(node.SizeBytes),
                RemovalSafety.SystemManaged,
                "Assume it's system-owned — don't delete unless you're certain.");

        // --- Program installation locations ---
        if (lower.Contains(@"\program files") )
            return Ctx(
                node.IsDirectory ? "Installed program folder" : $"Program {extLabel}file",
                "This belongs to an installed application. Removing parts of it can break that program.",
                "An installed application",
                ByteSize.Format(node.SizeBytes),
                RemovalSafety.SystemManaged,
                "Uninstall the program through Settings rather than deleting files here.");

        if (lower.Contains(@"\programdata"))
            return Ctx(
                node.IsDirectory ? "Shared app-data folder" : $"App-data {extLabel}file",
                "Shared application data used by programs for all users on this PC.",
                "Installed applications",
                ByteSize.Format(node.SizeBytes),
                RemovalSafety.Unknown,
                "Often app settings or caches; check which app owns it before removing.");

        // --- User profile locations ---
        if (lower.Contains(@"\appdata\local\temp"))
            return Ctx($"Temporary {extLabel}file".Trim(),
                "A temporary file in your per-user Temp folder. Usually leftover scratch data.",
                "Windows and various apps", "Varies; accumulates over time",
                RemovalSafety.SafeTransient,
                "Generally safe to clear; files in active use are skipped.");

        if (lower.Contains(@"\appdata\local"))
            return Ctx(node.IsDirectory ? "Local app-data folder" : $"Local app-data {extLabel}file",
                "Per-user application data stored on this machine (caches, settings, state).",
                "Apps you've used", ByteSize.Format(node.SizeBytes),
                RemovalSafety.Unknown,
                "Mix of caches and real settings — check the owning app before removing.");

        if (lower.Contains(@"\appdata\roaming"))
            return Ctx(node.IsDirectory ? "Roaming app-data folder" : $"Roaming app-data {extLabel}file",
                "Per-user settings that can roam with your account. Often real configuration.",
                "Apps you've used", ByteSize.Format(node.SizeBytes),
                RemovalSafety.PersonalData,
                "May hold settings you'd miss — back up or be sure before removing.");

        foreach (var (folder, label) in UserFolders)
        {
            if (lower.Contains($@"\{folder}\") || lower.EndsWith($@"\{folder}"))
                return Ctx(node.IsDirectory ? $"{label} folder" : $"File in {label}",
                    $"This is in your {label} folder and is most likely something you created or saved.",
                    "You", ByteSize.Format(node.SizeBytes),
                    RemovalSafety.PersonalData,
                    "Personal data — only remove if you're sure you don't need it.");
        }

        // --- Extension-only context when location is unremarkable ---
        if (!node.IsDirectory && ext.Length > 0)
        {
            var byCategory = ExtensionCategory(ext);
            if (byCategory is not null)
                return byCategory;
        }

        return null;
    }

    private static readonly (string Folder, string Label)[] UserFolders =
    [
        ("documents", "Documents"), ("downloads", "Downloads"), ("pictures", "Pictures"),
        ("music", "Music"), ("videos", "Videos"), ("desktop", "Desktop"),
    ];

    /// <summary>Broad context by extension family, for files in ordinary places.</summary>
    private static FileInfoResult? ExtensionCategory(string ext)
    {
        FileInfoResult C(string title, string desc, string size, RemovalSafety s, string note) => new()
        {
            Title = title, Description = desc, CreatedBy = "Various", TypicalSize = size,
            Safety = s, SafetyNote = note, IsKnown = false,
        };

        return ext switch
        {
            ".exe" or ".com" => C("Program", "An executable program you can run.", "KB to hundreds of MB",
                RemovalSafety.Unknown, "Deleting may break a program; uninstall instead if it's installed."),
            ".dll" => C("Code library", "A shared code library used by one or more programs.", "KB to a few MB",
                RemovalSafety.SystemManaged, "Don't delete — a program likely depends on it."),
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".heic" =>
                C("Image", "A picture or image file.", "KB to several MB",
                  RemovalSafety.PersonalData, "Likely something you saved — keep unless you're sure."),
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" =>
                C("Video", "A video file.", "MB to many GB",
                  RemovalSafety.PersonalData, "Often large personal media — review before removing."),
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" =>
                C("Audio", "A music or audio file.", "MB-sized",
                  RemovalSafety.PersonalData, "Likely personal media — keep unless you're sure."),
            ".docx" or ".doc" or ".xlsx" or ".xls" or ".pptx" or ".ppt" or ".pdf" or ".txt" or ".rtf" =>
                C("Document", "A document or office file.", "KB to a few MB",
                  RemovalSafety.PersonalData, "Likely your own work — don't remove unless you're sure."),
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" =>
                C("Compressed archive", "A compressed archive bundling other files.", "Varies widely",
                  RemovalSafety.Unknown, "Safe if you've already extracted what you need."),
            ".cache" or ".temp" => C("Cache/temp file", "Looks like cached or temporary data.", "Varies",
                RemovalSafety.SafeTransient, "Usually safe to remove."),
            ".old" or ".bak" => C("Backup/old copy", "A backup or previous version of something.", "Mirrors the original",
                RemovalSafety.Unknown, "Check you don't need the old copy before deleting."),
            _ => null,
        };
    }

    /// <summary>True if the catalog has a specific entry for this node.</summary>
    public static bool IsKnown(FileSystemNode node) => Describe(node).IsKnown;

    private static string GetExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[dot..].ToLowerInvariant();
    }

    private static FileInfoResult ToResult(FileKnowledge k, bool known) => new()
    {
        Title = k.Title,
        Description = k.Description,
        CreatedBy = k.CreatedBy,
        TypicalSize = k.TypicalSize,
        Safety = k.Safety,
        SafetyNote = k.SafetyNote,
        IsKnown = known,
    };

    private static FileInfoResult Generic(FileSystemNode node)
    {
        if (node.IsDirectory)
        {
            return new FileInfoResult
            {
                Title = "Folder",
                Description = "A folder containing other files and folders. " +
                              "Reclaim doesn't have specific information about this one.",
                CreatedBy = "Various",
                TypicalSize = ByteSize.Format(node.SizeBytes),
                Safety = RemovalSafety.Unknown,
                SafetyNote = "Review the contents before removing.",
                IsKnown = false,
            };
        }

        var ext = GetExtension(node.Name);
        var label = ext.Length > 0 ? $"{ext} file" : "File";
        return new FileInfoResult
        {
            Title = label,
            Description = "Reclaim doesn't have specific information about this file type. " +
                          "Check which program uses it before removing.",
            CreatedBy = "Unknown",
            TypicalSize = ByteSize.Format(node.SizeBytes),
            Safety = RemovalSafety.Unknown,
            SafetyNote = "Unknown — review before removing.",
            IsKnown = false,
        };
    }

    public static IReadOnlyList<FileKnowledge> All { get; } =
    [
        // ---- System files (exact names) ----
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "hiberfil.sys",
            Title = "Hibernation file",
            Description = "Stores the contents of memory when Windows hibernates, so your session can be restored.",
            CreatedBy = "Windows power management",
            TypicalSize = "Often several GB (roughly your RAM size)",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Don't delete directly. Disable via 'powercfg /hibernate off' if you want the space back.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "pagefile.sys",
            Title = "Page file (virtual memory)",
            Description = "Windows' virtual-memory swap file, used when physical RAM fills up.",
            CreatedBy = "Windows memory manager",
            TypicalSize = "Typically 1–several GB; managed automatically",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Don't delete. Adjust virtual-memory settings in System Properties if needed.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "swapfile.sys",
            Title = "Swap file",
            Description = "A companion to the page file used by modern Windows apps for fast suspend/resume.",
            CreatedBy = "Windows memory manager",
            TypicalSize = "Usually small (a few hundred MB)",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "System-managed — leave it alone.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "thumbs.db",
            Title = "Thumbnail cache",
            Description = "A cache of thumbnail images for a folder, so previews load quickly.",
            CreatedBy = "Windows Explorer",
            TypicalSize = "Small (KB to a few MB)",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to delete; Windows rebuilds it when needed.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "desktop.ini",
            Title = "Folder settings",
            Description = "Stores custom folder appearance settings (icon, view options).",
            CreatedBy = "Windows Explorer",
            TypicalSize = "Tiny (under 1 KB)",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Usually safe; the folder reverts to default appearance.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = ".DS_Store",
            Title = "macOS folder metadata",
            Description = "A macOS file storing folder view settings. Harmless leftover on Windows.",
            CreatedBy = "macOS Finder",
            TypicalSize = "Tiny (a few KB)",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to delete on Windows.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "ntuser.dat",
            Title = "User registry hive",
            Description = "Your user account's portion of the Windows registry.",
            CreatedBy = "Windows",
            TypicalSize = "Several MB to tens of MB",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Critical — never delete. Corrupting it can break your profile.",
        },

        // ---- Folders ----
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "node_modules",
            Title = "Node.js dependencies",
            Description = "Downloaded packages for a JavaScript/Node project. Can be huge and is fully regenerable.",
            CreatedBy = "npm / yarn / pnpm",
            TypicalSize = "Often hundreds of MB to several GB per project",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to delete; restore with 'npm install' in the project.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "__pycache__",
            Title = "Python bytecode cache",
            Description = "Compiled Python bytecode to speed up imports.",
            CreatedBy = "Python interpreter",
            TypicalSize = "Small (KB to a few MB)",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to delete; regenerated on next run.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "Windows.old",
            Title = "Previous Windows installation",
            Description = "A backup of your old Windows after a major update, kept so you can roll back.",
            CreatedBy = "Windows Update",
            TypicalSize = "Often 10–30+ GB",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Remove via Disk Cleanup ('Previous Windows installations'), not by hand.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "Temp",
            Title = "Temporary files folder",
            Description = "A scratch area where apps drop temporary files. Often not cleaned up.",
            CreatedBy = "Windows and many apps",
            TypicalSize = "Varies widely; can grow to GB",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Contents are generally safe to clear; files in active use are skipped.",
        },

        // ---- Extensions ----
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".tmp",
            Title = "Temporary file",
            Description = "A short-lived file an app created and ideally should have deleted.",
            CreatedBy = "Various applications",
            TypicalSize = "Usually small, but they accumulate",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Generally safe to delete if no app is mid-task.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".log",
            Title = "Log file",
            Description = "A text record of what a program did, used for troubleshooting.",
            CreatedBy = "Applications and the OS",
            TypicalSize = "KB to many MB if long-running",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Usually safe to delete; you lose past diagnostic history.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".bak",
            Title = "Backup file",
            Description = "A backup copy an app made before changing something.",
            CreatedBy = "Various applications",
            TypicalSize = "Mirrors the original file's size",
            Safety = RemovalSafety.Unknown,
            SafetyNote = "Check you don't need the backup before deleting.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".dmp",
            Title = "Crash dump",
            Description = "A snapshot of memory written when a program or Windows crashed.",
            CreatedBy = "Windows error reporting",
            TypicalSize = "MB to several GB for full memory dumps",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Safe to delete unless you're actively debugging the crash.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".iso",
            Title = "Disc image",
            Description = "A full image of a CD/DVD/USB, often an installer you downloaded.",
            CreatedBy = "Downloads / imaging tools",
            TypicalSize = "Hundreds of MB to several GB",
            Safety = RemovalSafety.PersonalData,
            SafetyNote = "Keep if you still need to install from it; otherwise safe.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".msi",
            Title = "Windows installer package",
            Description = "An installer for a Windows program.",
            CreatedBy = "Software downloads",
            TypicalSize = "MB to hundreds of MB",
            Safety = RemovalSafety.PersonalData,
            SafetyNote = "Safe to delete after installing, unless you want to reinstall later.",
        },

        // ---- More system files (exact names) ----
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "bootmgr",
            Title = "Windows Boot Manager",
            Description = "The program that starts Windows when your PC powers on.",
            CreatedBy = "Windows",
            TypicalSize = "Small (KB)",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Critical — deleting it stops Windows from booting.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "MEMORY.DMP",
            Title = "Full system crash dump",
            Description = "A complete snapshot of memory written when Windows had a blue-screen crash.",
            CreatedBy = "Windows error reporting",
            TypicalSize = "Can be very large (GB)",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Safe to delete unless you're diagnosing a crash.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "Thumbs.db:encryptable",
            Title = "Thumbnail cache",
            Description = "A thumbnail cache variant. Safe leftover.",
            CreatedBy = "Windows Explorer",
            TypicalSize = "Small",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to delete; rebuilt automatically.",
        },
        new() {
            MatchKind = KnowledgeMatch.ExactName, Token = "ntuser.dat.log1",
            Title = "Registry hive log",
            Description = "A transaction log protecting your user registry hive against corruption.",
            CreatedBy = "Windows",
            TypicalSize = "Small to a few MB",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Leave it — it guards your profile's registry.",
        },

        // ---- More folders ----
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "$Recycle.Bin",
            Title = "Recycle Bin storage",
            Description = "Where deleted files are held until you empty the Recycle Bin.",
            CreatedBy = "Windows",
            TypicalSize = "As large as your deleted files",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Empty the Recycle Bin normally rather than deleting this folder.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "System Volume Information",
            Title = "System restore data",
            Description = "Hidden system folder holding restore points and volume metadata.",
            CreatedBy = "Windows",
            TypicalSize = "Can be several GB",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Managed by Windows; adjust via System Protection, don't delete.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = ".git",
            Title = "Git repository data",
            Description = "Version-control history and metadata for a Git project.",
            CreatedBy = "Git",
            TypicalSize = "KB to hundreds of MB depending on history",
            Safety = RemovalSafety.PersonalData,
            SafetyNote = "Deleting loses the project's entire local version history.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "bin",
            Title = "Build output (bin)",
            Description = "Compiled program output, commonly from .NET or Java builds.",
            CreatedBy = "A compiler / build tool",
            TypicalSize = "MB-sized",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Usually safe; rebuilt when you compile the project again.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "obj",
            Title = "Build intermediates (obj)",
            Description = "Intermediate compiler files from a .NET build.",
            CreatedBy = "MSBuild / .NET",
            TypicalSize = "MB-sized",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to delete; regenerated on the next build.",
        },
        new() {
            MatchKind = KnowledgeMatch.FolderName, Token = "Prefetch",
            Title = "Windows Prefetch",
            Description = "Data Windows uses to start frequently-used programs faster.",
            CreatedBy = "Windows",
            TypicalSize = "Small (a few MB)",
            Safety = RemovalSafety.SafeRegenerates,
            SafetyNote = "Safe to clear, but it rebuilds and gives little lasting benefit.",
        },

        // ---- More extensions ----
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".sys",
            Title = "System driver/module",
            Description = "A low-level Windows driver or kernel module.",
            CreatedBy = "Windows / hardware drivers",
            TypicalSize = "KB to a few MB",
            Safety = RemovalSafety.SystemManaged,
            SafetyNote = "Don't delete — needed by Windows or your hardware.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".cab",
            Title = "Cabinet archive",
            Description = "A compressed archive Windows uses for updates and drivers.",
            CreatedBy = "Windows / installers",
            TypicalSize = "KB to hundreds of MB",
            Safety = RemovalSafety.Unknown,
            SafetyNote = "Often an update/driver package; check before removing.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".etl",
            Title = "Event trace log",
            Description = "A diagnostic trace log produced by Windows performance tools.",
            CreatedBy = "Windows tracing",
            TypicalSize = "MB-sized",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Safe to delete unless you're analyzing a trace.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".chk",
            Title = "Recovered file fragment",
            Description = "Data recovered by CHKDSK from a damaged disk; usually unusable.",
            CreatedBy = "Windows CHKDSK",
            TypicalSize = "Varies",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Almost always safe to delete.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".crdownload",
            Title = "Partial download (Chrome)",
            Description = "An incomplete file still being downloaded by Chrome.",
            CreatedBy = "Google Chrome",
            TypicalSize = "Up to the final file size",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Safe to delete if the download was abandoned.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".part",
            Title = "Partial download",
            Description = "An incomplete download (Firefox and other tools).",
            CreatedBy = "Browsers / downloaders",
            TypicalSize = "Up to the final file size",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Safe to delete if you don't intend to resume.",
        },
        new() {
            MatchKind = KnowledgeMatch.Extension, Token = ".err",
            Title = "Error log",
            Description = "A file recording errors from some program.",
            CreatedBy = "Various applications",
            TypicalSize = "Small",
            Safety = RemovalSafety.SafeTransient,
            SafetyNote = "Usually safe to delete.",
        },
    ];
}
