# Reclaim

A fast, open-source disk-space analyzer and cleanup tool for Windows — see
exactly what is using your storage, understand what each file is, and safely
reclaim space.

Reclaim scans a folder or drive and shows the results as a sortable tree and a
treemap where every file is a rectangle proportional to its size. Beyond
visualization, it identifies reclaimable space, explains what individual files
are, finds duplicates, and can carefully remove what you choose — always
recoverable by default.

**Status: v0.10 — a complete, usable tool.** Storage visualization, cleanup
analysis, cautious deletion, file descriptions, duplicate detection, and
Recycle Bin management are all in place.

## Features

### Storage mode
- **Parallel scanner** that walks a drive in seconds and reports size, file, and
  folder counts. Unreadable folders (access denied) are shown and excluded from
  totals rather than failing the scan; reparse points (junctions, symlinks,
  OneDrive placeholders) are skipped to avoid double-counting.
- **Treemap** with labels on blocks; double-click any block to jump straight to
  the folder it lives in, **← Back** to step out.
- **Tree and list views** sharing one focus, with a clickable breadcrumb.
- A **declutter slider** to hide files below a chosen size.
- **"What is this?" file descriptions**: select any file or folder and a panel
  explains what it is, what created it, its typical size, and whether it's safe
  to remove. Knows common system files, folders, and extensions, and infers
  sensible context from a file's location (e.g. anything under System32 is
  flagged as a Windows system file) even when it has no specific entry.
- **Manual deletion** via right-click: delete a file, empty a folder's contents
  (keeping the folder), or delete a whole folder. Recoverable by default.

### Cleanup mode
- **Reclaimable-space analysis**: a bundled rule catalog identifies shader
  caches, thumbnails, temp files, crash dumps, browser and dev-tool caches, and
  system-maintenance areas, each with a safety tier and plain-language reason.
- **Cautious deletion**: select items per-file or per-category and clean them.
  Safe caches go to the **Recycle Bin** by default; permanent deletion is an
  explicit opt-in. Matched cache folders keep the folder and remove only its
  contents. System items needing official tools (DISM, Disk Cleanup) are listed
  but refused in-app, with the official command shown instead.
- **Duplicate file finder**: detects byte-for-byte identical files (size-first,
  so only genuine candidates are hashed), then lets you keep one copy and
  recycle the rest.
- **Empty the Recycle Bin** from within the app, with its current size shown and
  a confirmation before emptying.

### Safety
Deletion logic lives in a tested engine, not the UI, so its rules can't be
bypassed. A hard, structurally-matched exclusion list blocks system-critical
roots (Windows, System32, SysWOW64, WinSxS, Program Files, ProgramData, the
Users root, Windows.old, drive roots) on any drive. Everything is confirmed, and
Recycle Bin is the default so mistakes are recoverable.

### Running as administrator
Reclaim starts as a normal user. A **Restart as admin** button (with a clear
warning) relaunches it elevated so scans can read protected system folders; an
**Administrator** badge shows when elevated. Elevation is always an explicit
choice.

### And a little fun
Double-click the **RC** logo for a hidden minigame — you'll randomly get one of
two 8-bit games with chiptune music. Purely cosmetic; it never touches real
files.

## Installing

Download `Reclaim.exe` from the [Releases](../../releases) page and run it. It's
a self-contained single file — no installer, and no .NET runtime required on the
machine.

Because the app isn't code-signed, the first run may show a Windows SmartScreen
warning ("Windows protected your PC"). Click **More info → Run anyway**. This is
normal for unsigned applications.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

Run from source:

```
dotnet run --project src/Reclaim.App
```

Build a portable single-file **Reclaim.exe** (no install, no runtime needed):

```
dotnet publish src/Reclaim.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

or just double-click `publish.cmd`. The exe lands in `publish/Reclaim.exe`
(~70 MB — it embeds the .NET runtime).

Releases are built automatically in the cloud via GitHub Actions when a version
tag is pushed — see [PUBLISHING.md](PUBLISHING.md). This means you never have to
build the exe locally to distribute it.

The core library and its tests build and run on any platform:

```
dotnet run --project src/Reclaim.Core.Tests
```

There are **zero package dependencies** — only the .NET SDK is needed. For a
tool that deletes files, an auditable, dependency-free codebase is a feature.

## Architecture

```
src/
├── Reclaim.Core/          Platform-agnostic engine (net8.0)
│   ├── Scanning/          IScanner, DirectoryScanner, FileSystemNode, FlatList
│   ├── Treemap/           Squarified treemap layout (pure math, unit tested)
│   ├── Cleanup/           Rules, analyzer, and the safety-critical DeletionEngine
│   ├── Knowledge/         File-description catalog and location-aware resolver
│   ├── Duplicates/        Size-first duplicate detector
│   └── Formatting/        ByteSize
├── Reclaim.App/           WPF UI (net8.0-windows)
│   ├── Controls/          TreemapControl (renderer over Core's Squarifier)
│   ├── Services/          Native shell integration (icons, deletion, Recycle
│   │                      Bin, elevation, hashing) — the only OS-specific code
│   └── ViewModels/        MVVM
└── Reclaim.Core.Tests/    Test harness (dotnet run; exit code 0 = pass)
```

The scanner parallelizes the shallow directory levels and reads file sizes
directly from directory enumeration — one enumeration pass per directory, no
per-file stat calls. `IScanner` exists so a future NTFS MFT scanner can slot in
beside the directory walker without touching the UI.

All destructive operations are isolated behind small injected interfaces
(`IFileRemover`, `IFileHasher`), so the dangerous code is tiny, swappable, and
testable with fakes. The test harness covers the scanner, treemap math, cleanup
analysis, deletion-engine safety, file knowledge, and duplicate detection.

## Roadmap

Done: scanner + treemap, reclaimable-space detection, cautious deletion, manual
deletion, file descriptions, duplicate finder, Recycle Bin management, optional
elevation, self-contained distributable build.

Possible next steps:
- **Fast NTFS scan**: read the Master File Table directly for whole-volume scans
  in seconds (requires elevation; falls back to the directory walker).
- **Live/community ruleset**: move cleanup rules to a schema-validated,
  separately-updatable source so cleanup knowledge can stay current without
  shipping a new app build.
- **Large-and-old file finder**, scan history/comparison, and CSV/JSON export.

## License

MIT
