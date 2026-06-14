# Reclaim

A fast, open-source disk-space analyzer and cleanup tool for Windows — see
exactly what is using your storage, understand what each file is, and safely
reclaim space.

Reclaim scans a folder or drive and shows the results as a sortable tree and a
colour-coded treemap where every file is a rectangle proportional to its size.
Beyond visualization, it identifies reclaimable space, explains what individual
files are, finds duplicate and forgotten files, and can carefully remove what
you choose — always recoverable by default.

**Status: 1.0 — stable.** Storage visualization, cleanup analysis, cautious
deletion, file descriptions, duplicate detection, a large-and-old file finder,
CSV/JSON export, Recycle Bin management, a settings dialog, optional elevation,
crash reporting, and a self-contained distributable build are all in place.

## Features

### Storage mode
- **Parallel scanner** that walks a drive in seconds and reports size, file, and
  folder counts. It reads each file's size and last-modified time in a single
  enumeration pass (no per-file stat calls). Unreadable folders (access denied)
  are shown and excluded from totals rather than failing the scan; reparse points
  (junctions, symlinks, OneDrive placeholders) are skipped to avoid double-counting.
- **Folder-coloured treemap**: every top-level folder gets its own hue and all of
  its contents share that colour family, so at a glance you can see which big
  folders (Windows, a game library, your downloads) are eating space. Colours are
  anchored to the scan root, so they stay consistent as you drill in. Double-click
  any block to jump to the folder it lives in; **← Back** steps out.
- **Tree and list views** sharing one focus, with a clickable breadcrumb.
- A **declutter slider** to hide files below a chosen size.
- **"What is this?" file descriptions**: select any file or folder and a panel
  explains what it is, what created it, its typical size, and whether it's safe
  to remove. It knows common system files, folders, and extensions, and infers
  sensible context from a file's location (e.g. a file under System32 is described
  as a Windows system file) even when it has no specific entry.
- **Manual deletion** via right-click: delete a file, empty a folder's contents
  (keeping the folder), or delete a whole folder. Recoverable by default.

### Cleanup mode
- **Reclaimable-space analysis**: a bundled rule catalog (~30 rules) identifies
  shader caches, thumbnails, temp files, crash dumps, browser caches (Chrome,
  Edge, Firefox, Brave, Opera, Vivaldi), developer and package-manager caches
  (npm, Yarn, NuGet, pip, Gradle, Maven, Cargo, Go, VS Code), game-launcher
  caches (Steam, Epic, Battle.net, DirectX), and system-maintenance areas — each
  with a safety tier and a plain-language reason.
- **Accurate accounting**: cleanup reports the bytes actually freed and separately
  counts files that were skipped because they were in use, so the numbers are
  honest. The pre-clean estimate is shown as "up to X" since in-use files are
  skipped.
- **Cautious deletion**: select items per-file or per-category and clean them.
  Safe caches go to the **Recycle Bin** by default; permanent deletion is an
  explicit opt-in. Matched cache folders keep the folder and remove only their
  contents. System items needing official tools (DISM, Disk Cleanup) are listed
  but refused in-app, with the official command shown instead.
- **Duplicate file finder**: detects byte-for-byte identical files using a fast
  three-stage strategy (group by size, then a cheap content-prefix check, then a
  full hash only on the survivors), so it doesn't read entire files needlessly.
  Optionally limit the scan to a chosen folder, watch progress, and cancel it with
  the main Cancel button. Results let you keep one copy and recycle the rest — and
  it verifies on disk that other copies still exist before deleting, so it can't
  remove your last remaining copy if the scan was out of date.
- **Large & old file finder**: surfaces big files you haven't modified in a long
  time (size and age thresholds are adjustable) — the forgotten downloads, old
  videos, and stale backups worth reviewing.
- **Empty the Recycle Bin** from within the app, with its current size shown and
  a confirmation before emptying.

### Export & settings
- **Export a scan** to CSV (a flat, spreadsheet-friendly row per file and folder)
  or JSON (the nested tree, structure preserved) from the toolbar.
- **Settings** (the gear icon): choose the default deletion mode (Recycle Bin vs
  permanent) and whether to remember the last scanned folder between launches.

### Safety
Deletion logic lives in a tested engine, not the UI, so its rules can't be
bypassed. Convenience features (the duplicate finder, the large-and-old finder,
and manual deletion) classify every file's location into three trust levels:

- **Protected** — genuinely OS-critical locations (anything inside `C:\Windows`,
  plus drive roots, the Users root, Windows.old, Recycle Bin storage, and System
  Volume Information). These can never be deleted through the app.
- **System (warn)** — installed-program and shared-data locations (Program Files,
  Program Files (x86), ProgramData). These hold real software *and* removable
  game/app content, so they're deletable but always behind a clear warning.
- **Normal** — your own files, deleted with a simple confirmation.

Everything destructive is confirmed, and the Recycle Bin is the default so
mistakes are recoverable.

### Running as administrator
Reclaim starts as a normal user. A **Restart as admin** button (with a clear
warning) relaunches it elevated so scans can read protected system folders; an
**Administrator** badge shows when elevated. Elevation is always an explicit
choice.

### Crash reporting
If Reclaim hits an unhandled error, it writes a local diagnostic report. On the
next launch it shows you that report and lets you **send it to the developer**
(it opens a pre-filled report in your browser for you to review and submit) or
copy it. Nothing is ever sent automatically, and you see exactly what would be
shared before sending. Note: hard kills (Task Manager "End task"), power loss,
and native runtime crashes cannot be captured by any in-process reporter.

### And a little fun
Double-click the **RC** logo for a hidden minigame — you'll randomly get one of
two 8-bit games with chiptune music and an adjustable volume. Purely cosmetic;
it never touches real files.

## Installing

Download `Reclaim.exe` from the [Releases](../../releases) page and run it. It's
a self-contained single file — no installer, and no .NET runtime required on the
machine.

Because the app isn't code-signed, the first run may show a Windows SmartScreen
warning ("Windows protected your PC"). Click **More info → Run anyway**. This is
normal for unsigned applications.

### Code signing

Reclaim is applying to the [SignPath Foundation](https://signpath.org)'s free
code-signing program for open-source projects. Once approved, official release
builds will be signed automatically in CI, which reduces the "unknown publisher"
warning over time. See [CODE_SIGNING.md](CODE_SIGNING.md) for the full policy.

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

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
│   ├── Cleanup/           Rules, analyzer, the safety-critical DeletionEngine,
│   │                      and the location-trust classifier
│   ├── Knowledge/         File-description catalog and location-aware resolver
│   ├── Duplicates/        Size-first / prefix / full-hash duplicate detector
│   ├── LargeOld/          Large-and-old file finder
│   └── Formatting/        ByteSize
├── Reclaim.App/           WPF UI (net8.0-windows)
│   ├── Controls/          TreemapControl (renderer over Core's Squarifier)
│   ├── Services/          Native shell integration (icons, deletion, Recycle
│   │                      Bin, elevation, hashing) and diagnostics — the only
│   │                      OS-specific code
│   └── ViewModels/        MVVM
└── Reclaim.Core.Tests/    Test harness (dotnet run; exit code 0 = pass)
```

The scanner parallelizes the shallow directory levels and reads file sizes and
timestamps directly from directory enumeration — one enumeration pass per
directory, no per-file stat calls. `IScanner` exists so a future NTFS MFT scanner
can slot in beside the directory walker without touching the UI.

All destructive operations are isolated behind small injected interfaces
(`IFileRemover`, `IFileHasher`), so the dangerous code is tiny, swappable, and
testable with fakes. The test harness covers the scanner, treemap math, cleanup
analysis, deletion-engine safety, the location-trust classifier, file knowledge,
duplicate detection, and the large-and-old finder.

## Roadmap

Done (1.0): scanner + folder-coloured treemap, reclaimable-space detection,
cautious and manual deletion with a location-trust safety model, file
descriptions, duplicate finder (fast hashing, scope, cancel), large-and-old file
finder, CSV/JSON export, Recycle Bin management, a settings dialog, optional
elevation, crash reporting, and a self-contained distributable build with
automated GitHub releases.

Possible next steps (post-1.0):
- **Fast NTFS scan**: read the Master File Table directly for whole-volume scans
  in seconds (requires elevation; NTFS only; falls back to the directory walker).
- **Scan history / comparison**: save a scan and compare later to see what grew.
- **File-type breakdown**: aggregate space by file type across the scan.
- **Light/dark theme toggle**: the colour system is already centralized for this.

## License

MIT
