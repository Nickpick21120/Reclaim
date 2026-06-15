using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Reclaim.App.Controls;
using Reclaim.App.Services;
using Reclaim.Core.Cleanup;
using Reclaim.Core.Formatting;
using Reclaim.Core.Knowledge;
using Reclaim.Core.Scanning;

namespace Reclaim.App.ViewModels;

public enum AppMode
{
    Storage,
    Cleanup,
}

public enum RightPaneView
{
    Treemap,
    LargestItems,
}

public sealed class MainViewModel : ViewModelBase
{
    private readonly IScanner _scanner = new DirectoryScanner();
    private CancellationTokenSource? _cts;

    private string _targetPath = "C:\\";
    private bool _isScanning;
    private string _statusText = "Pick a folder or drive, then scan.";
    private string _summaryText = "";
    private NodeViewModel? _rootViewModel;
    private FileSystemNode? _treemapRoot;
    private string _treemapPath = "";
    private FileSystemNode? _scannedRoot;
    private RightPaneView _view = RightPaneView.Treemap;
    private FlatListKind _flatKind = FlatListKind.Files;
    private double _minSizeTier;  // slider position 0..MaxTier
    private AppMode _mode = AppMode.Storage;
    private CleanupReport? _cleanupReport;
    private string _cleanupSummary = "";
    private readonly AppSettings _settings;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _view = _settings.DefaultView == "List"
            ? RightPaneView.LargestItems
            : RightPaneView.Treemap;
        _permanentDelete = _settings.DefaultPermanentDelete;
        if (_settings.RememberLastFolder && !string.IsNullOrWhiteSpace(_settings.LastFolder))
            _targetPath = _settings.LastFolder;

        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsScanning);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsScanning);
        TreemapUpCommand = new RelayCommand(
            () => { if (TreemapRoot?.Parent is { } p) TreemapRoot = p; },
            () => TreemapRoot?.Parent is not null);
        ShowTreemapCommand = new RelayCommand(() => View = RightPaneView.Treemap);
        ShowLargestCommand = new RelayCommand(() => View = RightPaneView.LargestItems);
        ShowFilesCommand = new RelayCommand(() => FlatKind = FlatListKind.Files);
        ShowFoldersCommand = new RelayCommand(() => FlatKind = FlatListKind.Folders);
        StorageModeCommand = new RelayCommand(() => Mode = AppMode.Storage);
        CleanupModeCommand = new RelayCommand(() => Mode = AppMode.Cleanup);
        CleanSelectedCommand = new RelayCommand(CleanSelected, () => SelectedCleanupCount > 0 && !IsScanning);
        DeleteFileCommand = new RelayCommand<object>(o => DeleteFileManually(NodeOf(o)));
        DeleteContentsCommand = new RelayCommand<object>(o => DeleteFolderContentsManually(NodeOf(o)));
        DeleteFolderCommand = new RelayCommand<object>(o => DeleteFolderManually(NodeOf(o)));
        CleanThisFileCommand = new RelayCommand<object>(o => CleanSingleFile(o as FlatItemViewModel));
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsElevated);
        FindDuplicatesCommand = new RelayCommand(FindDuplicates, () => _scannedRoot is not null && !IsScanning);
        EmptyRecycleBinCommand = new RelayCommand(EmptyRecycleBin);
        ChooseDuplicateScopeCommand = new RelayCommand(
            () => ChooseDuplicateScopeRequested?.Invoke(),
            () => _scannedRoot is not null && !IsScanning);
        FindLargeOldCommand = new RelayCommand(FindLargeOld, () => _scannedRoot is not null && !IsScanning);
        ExportScanCommand = new RelayCommand(() => ExportScanRequested?.Invoke(),
            () => _scannedRoot is not null && !IsScanning);
        RefreshRecycleBin();
    }

    public RelayCommand FindDuplicatesCommand { get; }

    // ---- Recycle Bin ----
    public RelayCommand EmptyRecycleBinCommand { get; }

    private string _recycleBinText = "";
    /// <summary>Human summary of current Recycle Bin contents.</summary>
    public string RecycleBinText
    {
        get => _recycleBinText;
        private set => Set(ref _recycleBinText, value);
    }

    /// <summary>Refresh the Recycle Bin size/count display.</summary>
    public void RefreshRecycleBin()
    {
        var (bytes, items) = RecycleBin.Query();
        RecycleBinText = items <= 0
            ? "Recycle Bin is empty."
            : $"Recycle Bin holds {items:N0} item{(items == 1 ? "" : "s")} · {ByteSize.Format(bytes)}.";
    }

    private void EmptyRecycleBin()
    {
        var (bytes, items) = RecycleBin.Query();
        if (items <= 0)
        {
            RecycleBinText = "Recycle Bin is already empty.";
            return;
        }

        var ok = MessageBox.Show(
            $"Permanently empty the Recycle Bin?\n\n{items:N0} item{(items == 1 ? "" : "s")} · {ByteSize.Format(bytes)}\n\n" +
            "This can't be undone.",
            "Empty Recycle Bin", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK)
            return;

        var success = RecycleBin.Empty();
        RefreshRecycleBin();
        StatusText = success
            ? $"Emptied the Recycle Bin (freed {ByteSize.Format(bytes)})."
            : "Could not empty the Recycle Bin.";
    }

    /// <summary>Guidance shown under the duplicates section, reflecting whether a
    /// scan is available to search.</summary>
    public string DuplicatesHint => _scannedRoot is null
        ? "Run a scan first (top bar), then scan for duplicates."
        : "Ready — this searches the folders from your current scan.";

    /// <summary>Raised when duplicates are found, so the window can open. Carries
    /// the report; the view subscribes and shows the results window.</summary>
    public event Action<Reclaim.Core.Duplicates.DuplicateReport>? DuplicatesReady;

    // ---- Large & old files ----
    public RelayCommand FindLargeOldCommand { get; private set; } = null!;
    public event Action<Reclaim.Core.LargeOld.LargeOldReport>? LargeOldReady;

    // ---- Export ----
    public RelayCommand ExportScanCommand { get; private set; } = null!;
    /// <summary>Raised when the user wants to export; the view shows a Save dialog
    /// and calls WriteExport with the chosen path and format.</summary>
    public event Action? ExportScanRequested;

    /// <summary>Serialize the current scan to the chosen path. Format is inferred
    /// from the extension (.json → JSON, else CSV). Returns a status message.</summary>
    public void WriteExport(string path)
    {
        if (_scannedRoot is null)
            return;
        try
        {
            var isJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            var content = isJson
                ? Reclaim.Core.Export.ScanExporter.ToJson(_scannedRoot)
                : Reclaim.Core.Export.ScanExporter.ToCsv(_scannedRoot);
            System.IO.File.WriteAllText(path, content);
            StatusText = $"Exported scan to {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    private int _largeOldMinSizeMb = 100;
    /// <summary>"Large" threshold in MB (user-adjustable).</summary>
    public int LargeOldMinSizeMb
    {
        get => _largeOldMinSizeMb;
        set => Set(ref _largeOldMinSizeMb, Math.Max(1, value));
    }

    private int _largeOldMinMonths = 6;
    /// <summary>"Old" threshold in months (user-adjustable).</summary>
    public int LargeOldMinMonths
    {
        get => _largeOldMinMonths;
        set => Set(ref _largeOldMinMonths, Math.Max(1, value));
    }

    private async void FindLargeOld()
    {
        if (_scannedRoot is null)
            return;

        IsScanning = true;
        StatusText = "Looking for large, old files…";
        BeginProgress(indeterminate: true);
        Reclaim.Core.LargeOld.LargeOldReport report;
        try
        {
            var finder = new Reclaim.Core.LargeOld.LargeOldFinder
            {
                MinSizeBytes = (long)LargeOldMinSizeMb * 1024 * 1024,
                MinAgeDays = LargeOldMinMonths * 30,
            };
            var root = _scannedRoot;
            var now = DateTime.UtcNow;
            report = await Task.Run(() => finder.Find(root, now));
        }
        catch (Exception ex)
        {
            StatusText = $"Large/old scan failed: {ex.Message}";
            return;
        }
        finally
        {
            EndProgress();
            IsScanning = false;
        }

        StatusText = report.Files.Count == 0
            ? "No large, old files matched those thresholds."
            : $"Found {report.Files.Count} large, old file(s) — {ByteSize.Format(report.TotalBytes)} total.";
        LargeOldReady?.Invoke(report);
    }

    /// <summary>Called by the duplicates window after it removes files, so the
    /// main views reflect the pruned tree.</summary>
    public void NotifyExternalChange() => RefreshAfterPrune();

    /// <summary>Optional subfolder to limit the duplicate scan to. Null = whole tree.</summary>
    private FileSystemNode? _duplicateScope;

    public RelayCommand ChooseDuplicateScopeCommand { get; private set; } = null!;

    /// <summary>Raised when the user wants to pick a folder to scope the dup scan;
    /// the view shows a folder picker and calls SetDuplicateScope with the result.</summary>
    public event Action? ChooseDuplicateScopeRequested;

    private string _duplicateScopeText = "Scanning the whole scanned tree.";
    public string DuplicateScopeText
    {
        get => _duplicateScopeText;
        private set => Set(ref _duplicateScopeText, value);
    }

    /// <summary>Find the scanned node matching a path, so a chosen folder can scope
    /// the scan to just that subtree. Returns null if it isn't in the scan.</summary>
    private FileSystemNode? FindNode(FileSystemNode node, string fullPath)
    {
        if (string.Equals(node.FullPath.TrimEnd('\\'), fullPath.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
            return node;
        if (!node.IsDirectory)
            return null;
        foreach (var child in node.Children)
        {
            var hit = FindNode(child, fullPath);
            if (hit is not null)
                return hit;
        }
        return null;
    }

    /// <summary>Set the scan scope to a specific folder (or clear it to whole-tree).</summary>
    public void SetDuplicateScope(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || _scannedRoot is null)
        {
            _duplicateScope = null;
            DuplicateScopeText = "Scanning the whole scanned tree.";
            return;
        }

        var node = FindNode(_scannedRoot, folderPath);
        if (node is null)
        {
            _duplicateScope = null;
            DuplicateScopeText = "That folder isn't part of the current scan — scanning the whole tree.";
            return;
        }
        _duplicateScope = node;
        DuplicateScopeText = $"Limiting the search to: {node.FullPath}";
    }

    private async void FindDuplicates()
    {
        if (_scannedRoot is null)
            return;

        IsScanning = true;
        StatusText = "Analyzing files for duplicates…";
        BeginProgress(indeterminate: true); // until the hash-total is known
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Reclaim.Core.Duplicates.DuplicateReport report;
        try
        {
            var finder = new Reclaim.Core.Duplicates.DuplicateFinder(new Sha256FileHasher());
            var root = _duplicateScope ?? _scannedRoot;

            // Marshal progress reports onto the UI thread.
            var progress = new Progress<(int done, int total)>(p =>
            {
                ReportProgress(p.done, p.total);
                StatusText = p.total > 0
                    ? $"Hashing files for duplicates… {p.done:N0}/{p.total:N0}"
                    : "Analyzing files for duplicates…";
            });

            report = await Task.Run(() => finder.Find(root, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Duplicate scan canceled.";
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate scan failed: {ex.Message}";
            return;
        }
        finally
        {
            EndProgress();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }

        StatusText = report.Groups.Count == 0
            ? "No duplicate files found."
            : $"Found {report.Groups.Count} duplicate group(s) — {ByteSize.Format(report.TotalReclaimableBytes)} reclaimable.";
        DuplicatesReady?.Invoke(report);
    }

    /// <summary>Whether Reclaim is currently running with administrator rights.
    /// When true, scans can read most protected directories.</summary>
    public bool IsElevated { get; } = Elevation.IsElevated();

    private FileInfoResult? _selectedInfo;
    private string _selectedName = "";

    /// <summary>Plain-language description of the currently-selected item, shown
    /// in the Storage info panel. Null when nothing is selected.</summary>
    public FileInfoResult? SelectedInfo
    {
        get => _selectedInfo;
        private set { _selectedInfo = value; Raise(nameof(SelectedInfo)); Raise(nameof(HasSelectedInfo)); }
    }

    public bool HasSelectedInfo => _selectedInfo is not null;

    public string SelectedName
    {
        get => _selectedName;
        private set => Set(ref _selectedName, value);
    }

    /// <summary>Resolve and show the description for a node (or clear it).</summary>
    public void DescribeSelection(FileSystemNode? node)
    {
        if (node is null)
        {
            SelectedInfo = null;
            SelectedName = "";
            return;
        }
        SelectedName = node.Name;
        SelectedInfo = FileKnowledgeBase.Describe(node);
    }

    public RelayCommand RestartAsAdminCommand { get; }

    private void RestartAsAdmin()
    {
        if (IsElevated)
            return;

        var ok = MessageBox.Show(
            "Reclaim will restart and ask for administrator rights.\n\n" +
            "Running as administrator lets Reclaim scan protected system folders " +
            "that are otherwise unreadable. Be aware it also removes a layer of " +
            "Windows protection around system files, so take extra care with deletion.\n\n" +
            "Continue?",
            "Restart as administrator",
            MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (ok == MessageBoxResult.OK)
            Elevation.RestartElevated();
    }

    public RelayCommand<object> DeleteFileCommand { get; }
    public RelayCommand<object> DeleteContentsCommand { get; }
    public RelayCommand<object> DeleteFolderCommand { get; }
    public RelayCommand<object> CleanThisFileCommand { get; }

    /// <summary>Clean one reclaimable file from the Cleanup list view. Only acts
    /// on safe-tier reclaimable files; confirms, deletes, prunes, and refreshes.</summary>
    private async void CleanSingleFile(FlatItemViewModel? item)
    {
        if (item is null || !item.CanCleanThis)
            return;

        if (!ConfirmDelete($"Clean this file?\n\n{item.Name}  ({ByteSize.Format(item.SizeBytes)})",
                "Clean file", unvetted: false))
            return;

        var node = item.Node;
        await RunManualDelete(node.Name,
            (engine, mode) => engine.DeleteFile(node.FullPath, mode),
            () => node.RemoveFromTree());
    }

    /// <summary>Extracts the underlying scanned node from a row view model
    /// (tree NodeViewModel or list FlatItemViewModel).</summary>
    private static FileSystemNode? NodeOf(object? rowVm) => rowVm switch
    {
        NodeViewModel n => n.Node,
        FlatItemViewModel f => f.Node,
        _ => null,
    };

    public RelayCommand CleanSelectedCommand { get; }

    private bool _permanentDelete;
    /// <summary>When true, cleaning permanently deletes; otherwise to Recycle Bin.</summary>
    public bool PermanentDelete
    {
        get => _permanentDelete;
        set => Set(ref _permanentDelete, value);
    }

    /// <summary>The persisted settings, exposed so the Settings dialog can edit them.</summary>
    public AppSettings Settings => _settings;

    /// <summary>Re-apply settings after the Settings dialog saves (e.g. the deletion
    /// default may have changed).</summary>
    public void ApplySettings()
    {
        PermanentDelete = _settings.DefaultPermanentDelete;
    }

    /// <summary>Count of currently-selected, deletable findings across all categories.</summary>
    public int SelectedCleanupCount =>
        CleanupCategories.SelectMany(c => c.Findings).Count(f => f.IsSelected);

    public string CleanButtonText
    {
        get
        {
            var n = SelectedCleanupCount;
            if (n == 0) return "Clean selected";
            var bytes = CleanupCategories.SelectMany(c => c.Findings)
                .Where(f => f.IsSelected).Sum(f => f.SizeBytes);
            return $"Clean {n} item{(n == 1 ? "" : "s")} · {ByteSize.Format(bytes)}";
        }
    }

    /// <summary>Recompute selection-dependent UI. Call when a checkbox changes.</summary>
    public void RefreshSelectionState()
    {
        Raise(nameof(SelectedCleanupCount));
        Raise(nameof(CleanButtonText));
        CleanSelectedCommand.RaiseCanExecuteChanged();
    }

    public RelayCommand StorageModeCommand { get; }
    public RelayCommand CleanupModeCommand { get; }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand TreemapUpCommand { get; }
    public RelayCommand ShowTreemapCommand { get; }
    public RelayCommand ShowLargestCommand { get; }
    public RelayCommand ShowFilesCommand { get; }
    public RelayCommand ShowFoldersCommand { get; }

    public ObservableCollection<NodeViewModel> RootItems { get; } = [];
    public ObservableCollection<FlatItemViewModel> FlatItems { get; } = [];

    public ObservableCollection<CleanupCategoryViewModel> CleanupCategories { get; } = [];

    /// <summary>Storage (TreeSize) vs Cleanup (reclaimable analysis). Both share
    /// the same scan; switching to Cleanup analyzes the existing tree.</summary>
    public AppMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                Raise(nameof(IsStorageMode));
                Raise(nameof(IsCleanupMode));
                if (value == AppMode.Cleanup)
                {
                    RunCleanupAnalysis();
                    RefreshRecycleBin();
                }
                else if (View == RightPaneView.LargestItems)
                    RebuildFlatList(); // back to Storage: drop the reclaimable flags
            }
        }
    }

    public bool IsStorageMode => _mode == AppMode.Storage;
    public bool IsCleanupMode => _mode == AppMode.Cleanup;

    public string CleanupSummary
    {
        get => _cleanupSummary;
        private set => Set(ref _cleanupSummary, value);
    }

    public RightPaneView View
    {
        get => _view;
        set
        {
            if (Set(ref _view, value))
            {
                Raise(nameof(IsTreemapView));
                Raise(nameof(IsLargestView));
                if (value == RightPaneView.LargestItems)
                    RebuildFlatList();

                // Remember the last-used view for next launch (quiet, debounced
                // by the fact that this only fires on an actual change).
                var pref = value == RightPaneView.LargestItems ? "List" : "Treemap";
                if (_settings.DefaultView != pref)
                {
                    _settings.DefaultView = pref;
                    _settings.Save();
                }
            }
        }
    }

    public bool IsTreemapView => _view == RightPaneView.Treemap;
    public bool IsLargestView => _view == RightPaneView.LargestItems;

    public FlatListKind FlatKind
    {
        get => _flatKind;
        set
        {
            if (Set(ref _flatKind, value))
            {
                Raise(nameof(IsFilesKind));
                Raise(nameof(IsFoldersKind));
                Raise(nameof(FlatHeaderText));
                RebuildFlatList();
            }
        }
    }

    public bool IsFilesKind => _flatKind == FlatListKind.Files;
    public bool IsFoldersKind => _flatKind == FlatListKind.Folders;

    /// <summary>Discrete, human-sensible size stops for the declutter slider.
    /// Index 0 means "off" (show everything).</summary>
    private static readonly long[] SizeStops =
    [
        0,
        100L * 1024,            // 100 KB
        250L * 1024,            // 250 KB
        500L * 1024,            // 500 KB
        1L * 1024 * 1024,       // 1 MB
        5L * 1024 * 1024,       // 5 MB
        10L * 1024 * 1024,      // 10 MB
        50L * 1024 * 1024,      // 50 MB
        100L * 1024 * 1024,     // 100 MB
        500L * 1024 * 1024,     // 500 MB
        1024L * 1024 * 1024,    // 1 GB
        5L * 1024 * 1024 * 1024 // 5 GB
    ];

    /// <summary>Maximum slider index.</summary>
    public double MaxSizeTier => SizeStops.Length - 1;

    /// <summary>Live slider position. Updates continuously during a drag and
    /// drives only the (cheap) label — NOT the treemap. Commit() promotes this
    /// to the actual threshold once the user settles, so the expensive layout
    /// recompute happens once per gesture instead of once per tick.</summary>
    public double MinSizeTier
    {
        get => _minSizeTier;
        set
        {
            if (Set(ref _minSizeTier, value))
                Raise(nameof(MinSizeLabel));
        }
    }

    /// <summary>Promote the live slider position to the committed threshold,
    /// triggering a single treemap rebuild. Called when the drag ends.</summary>
    public void CommitMinSize()
    {
        if (_committedTier != TierIndex)
        {
            _committedTier = TierIndex;
            Raise(nameof(MinFileBytes));
        }
    }

    private int _committedTier;

    private int TierIndex => Math.Clamp((int)Math.Round(_minSizeTier), 0, SizeStops.Length - 1);

    /// <summary>The committed stop converted to a byte threshold the treemap consumes.</summary>
    public long MinFileBytes => SizeStops[_committedTier];

    public string MinSizeLabel => TierIndex == 0
        ? "Hide files under: off"
        : $"Hide files under: {ByteSize.Format(SizeStops[TierIndex])}";

    public string FlatHeaderText => _flatKind == FlatListKind.Files
        ? "Largest files"
        : "Largest folders";

    public string TargetPath
    {
        get => _targetPath;
        set => Set(ref _targetPath, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                FindDuplicatesCommand.RaiseCanExecuteChanged();
                CleanSelectedCommand.RaiseCanExecuteChanged();
                ChooseDuplicateScopeCommand.RaiseCanExecuteChanged();
                FindLargeOldCommand.RaiseCanExecuteChanged();
                ExportScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ---- Progress bar (cleaning / duplicate scan) ----
    private bool _progressActive;
    private double _progressValue; // 0..100
    private bool _progressIndeterminate;

    /// <summary>True while a long operation with a progress bar is running.</summary>
    public bool ProgressActive
    {
        get => _progressActive;
        private set => Set(ref _progressActive, value);
    }

    /// <summary>Progress 0–100 for the status-bar progress bar.</summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    /// <summary>When true, the bar animates without a fixed value (e.g. during an
    /// initial "analyzing" phase before the total is known).</summary>
    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => Set(ref _progressIndeterminate, value);
    }

    private void BeginProgress(bool indeterminate = false)
    {
        ProgressIndeterminate = indeterminate;
        ProgressValue = 0;
        ProgressActive = true;
    }

    private void ReportProgress(int done, int total)
    {
        ProgressIndeterminate = false;
        ProgressValue = total > 0 ? Math.Clamp(done * 100.0 / total, 0, 100) : 0;
    }

    private void EndProgress()
    {
        ProgressActive = false;
        ProgressValue = 0;
        ProgressIndeterminate = false;
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    /// <summary>The folder both the treemap and the largest-items list are
    /// currently focused on. Changing it updates whichever view is visible and
    /// the breadcrumb, so toggling between views never loses your place.
    /// (Named TreemapRoot for back-compat with existing XAML bindings.)</summary>
    public FileSystemNode? TreemapRoot
    {
        get => _treemapRoot;
        set
        {
            if (Set(ref _treemapRoot, value))
            {
                TreemapPath = value?.FullPath ?? "";
                TreemapUpCommand.RaiseCanExecuteChanged();
                RebuildBreadcrumb();
                // Keep the textual list in sync with the focus folder.
                if (View == RightPaneView.LargestItems)
                    RebuildFlatList();
                Raise(nameof(HasFocus));
            }
        }
    }

    /// <summary>True once a scan has produced a focusable tree.</summary>
    public bool HasFocus => _treemapRoot is not null;

    public string TreemapPath
    {
        get => _treemapPath;
        private set => Set(ref _treemapPath, value);
    }

    /// <summary>Ancestry from the scanned root down to the focused node, for a
    /// clickable breadcrumb shown above each view.</summary>
    public ObservableCollection<BreadcrumbItem> Breadcrumb { get; } = [];

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        if (_treemapRoot is null)
            return;

        // Walk up to the scanned root, collecting nodes, then reverse.
        var chain = new List<FileSystemNode>();
        for (var n = _treemapRoot; n is not null; n = n.Parent)
        {
            chain.Add(n);
            if (_scannedRoot is not null && ReferenceEquals(n, _scannedRoot))
                break;
        }
        chain.Reverse();

        for (var i = 0; i < chain.Count; i++)
            Breadcrumb.Add(new BreadcrumbItem(chain[i], isLast: i == chain.Count - 1));
    }

    /// <summary>Navigate the shared focus to a breadcrumb entry.</summary>
    public void FocusOn(FileSystemNode node) => TreemapRoot = node;

    public NodeViewModel? RootViewModel
    {
        get => _rootViewModel;
        private set => Set(ref _rootViewModel, value);
    }

    private async Task ScanAsync()
    {
        var path = TargetPath.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        // Remember this folder for next launch, if the user opted in.
        if (_settings.RememberLastFolder)
        {
            _settings.LastFolder = path;
            _settings.Save();
        }

        _cts = new CancellationTokenSource();
        IsScanning = true;
        RootItems.Clear();
        FlatItems.Clear();
        CleanupCategories.Clear();
        CleanupSummary = "";
        _cleanupReport = null;
        RootViewModel = null;
        TreemapRoot = null;
        _scannedRoot = null;
        SummaryText = "";

        var progress = new Progress<ScanProgress>(p =>
            StatusText = $"Scanning…  {p.FilesScanned:N0} files · {ByteSize.Format(p.BytesSeen)} · {Shorten(p.CurrentPath)}");

        try
        {
            // Choose the scanner: experimental raw-MFT for whole-NTFS-drive scans
            // when the user opted in AND it's applicable; otherwise the reliable
            // directory walker. If MFT scanning throws, fall back rather than fail.
            IScanner scanner = _scanner;
            var usingMft = false;
            if (_settings.ExperimentalMftScan)
            {
                var canMft = MftScanner.CanScan(path, out var why);
                if (canMft)
                {
                    scanner = new MftScanner();
                    usingMft = true;
                    StatusText = "Scanning with the experimental MFT reader…";
                }
                else
                {
                    StatusText = $"MFT scan not used ({why}). Using the normal scanner…";
                }
            }

            ScanResult result;
            try
            {
                result = await scanner.ScanAsync(path, new ScanOptions(), progress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception mftEx) when (usingMft)
            {
                // The experimental path failed — fall back to the dependable scanner.
                StatusText = $"MFT scan failed ({mftEx.Message}). Falling back to the normal scanner…";
                result = await _scanner.ScanAsync(path, new ScanOptions(), progress, _cts.Token);
            }

            var rootVm = new NodeViewModel(result.Root) { IsExpanded = true };
            RootViewModel = rootVm;
            RootItems.Add(rootVm);
            _scannedRoot = result.Root;
            TreemapRoot = result.Root;
            Raise(nameof(DuplicatesHint));
            FindDuplicatesCommand.RaiseCanExecuteChanged();
            if (View == RightPaneView.LargestItems)
                RebuildFlatList();
            if (Mode == AppMode.Cleanup)
                RunCleanupAnalysis();

            SummaryText =
                $"{ByteSize.Format(result.Root.SizeBytes)} in {result.FilesScanned:N0} files, " +
                $"{result.DirectoriesScanned:N0} folders";
            StatusText =
                $"Done in {result.Elapsed.TotalSeconds:0.0}s" +
                (result.ErrorCount > 0 ? $" — {result.ErrorCount:N0} folders could not be read" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan canceled.";
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            StatusText = ex.Message;
            MessageBox.Show(ex.Message, "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Manually delete one item chosen from the Storage tree/list.
    /// Files are removed outright; for folders, callers choose contents-only or
    /// the whole folder. Explicit, single-target, confirmed; refuses protected
    /// roots. Recycle Bin unless the user opted into permanent.</summary>
    /// <summary>Stricter than the engine's root-only guard: blocks deletion of
    /// files anywhere inside protected system trees, and warns on system locations.
    /// Returns true if deletion may proceed. Used by manual delete + duplicates.</summary>
    private bool PassesLocationGuard(FileSystemNode node)
    {
        var trust = LocationTrustClassifier.Classify(node.FullPath);
        if (trust == LocationTrust.Protected)
        {
            ShowProtected(node.Name);
            return false;
        }
        if (trust == LocationTrust.System)
        {
            var warn = MessageBox.Show(
                "This is in a system location:\n\n" + node.FullPath +
                "\n\nIt may be needed by Windows or an installed program. Delete it anyway?",
                "System location — are you sure?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (warn != MessageBoxResult.OK)
                return false;
        }
        return true;
    }

    public async void DeleteFileManually(FileSystemNode? node)
    {
        if (node is null || node.IsDirectory)
            return;

        if (!DeletionEngine.CanDeleteFolder(node.FullPath) || !PassesLocationGuard(node))
        {
            return;
        }

        if (!ConfirmDelete($"Delete this file?\n\n{node.Name}  ({ByteSize.Format(node.SizeBytes)})",
                "Delete file", unvetted: false))
            return;

        await RunManualDelete(node.Name,
            (engine, mode) => engine.DeleteFile(node.FullPath, mode),
            () => node.RemoveFromTree());
    }

    public async void DeleteFolderContentsManually(FileSystemNode? node)
    {
        if (node is null || !node.IsDirectory)
            return;
        if (!DeletionEngine.CanDeleteFolder(node.FullPath) || !PassesLocationGuard(node))
            return;
        if (node.Children.Count == 0)
        {
            StatusText = $"\"{node.Name}\" is already empty.";
            return;
        }

        if (!ConfirmDelete(
                $"Empty this folder? Contents are removed; the folder is kept.\n\n" +
                $"{node.Name}  ({ByteSize.Format(node.SizeBytes)}, {node.FileCount:N0} files)",
                "Delete folder contents", unvetted: true))
            return;

        await RunManualDelete(node.Name,
            (engine, mode) => { engine.DeleteFolderContents(node, mode); return true; },
            () => node.ClearChildrenFromTree());
    }

    public async void DeleteFolderManually(FileSystemNode? node)
    {
        if (node is null || !node.IsDirectory)
            return;
        if (!DeletionEngine.CanDeleteFolder(node.FullPath) || !PassesLocationGuard(node))
            return;

        if (!ConfirmDelete(
                $"Delete this folder and everything in it?\n\n" +
                $"{node.Name}  ({ByteSize.Format(node.SizeBytes)}, {node.FileCount:N0} files)",
                "Delete folder", unvetted: true))
            return;

        await RunManualDelete(node.Name,
            (engine, mode) => engine.DeleteFolder(node.FullPath, mode),
            () => node.RemoveFromTree());
    }

    private void ShowProtected(string name) =>
        MessageBox.Show(
            $"\"{name}\" is a protected system location and can't be deleted here.",
            "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);

    private bool ConfirmDelete(string whatClause, string title, bool unvetted)
    {
        var dest = PermanentDelete ? "Permanent — can't be undone." : "Goes to Recycle Bin.";
        var note = unvetted ? "\nNot a known-safe cache." : "";
        return MessageBox.Show(
            $"{whatClause}\n\n{dest}{note}",
            title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private async Task RunManualDelete(string name, Func<DeletionEngine, DeletionMode, bool> action,
                                       Action prune)
    {
        var engine = new DeletionEngine(new ShellFileRemover());
        var mode = PermanentDelete ? DeletionMode.Permanent : DeletionMode.RecycleBin;

        IsScanning = true;
        StatusText = $"Deleting {name}…";
        bool ok;
        try
        {
            ok = await Task.Run(() => action(engine, mode));
        }
        catch (Exception ex)
        {
            ok = false;
            StatusText = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }

        if (ok)
        {
            StatusText = $"Deleted {name}" +
                         (mode == DeletionMode.RecycleBin ? " (sent to Recycle Bin)" : " (permanent)");
            // Update the in-memory tree and refresh views — no disk rescan.
            prune();
            RefreshAfterPrune();
        }
    }

    private async void CleanSelected()
    {
        var selected = CleanupCategories
            .SelectMany(c => c.Findings)
            .Where(f => f.IsSelected)
            .Select(f => f.Finding)
            .ToList();

        if (selected.Count == 0)
            return;

        var engine = new DeletionEngine(new ShellFileRemover());
        var plan = engine.Plan(selected);

        if (plan.AllowedCount == 0)
        {
            MessageBox.Show(
                "None of the selected items can be cleaned from within Reclaim.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mode = PermanentDelete ? DeletionMode.Permanent : DeletionMode.RecycleBin;
        var dest = mode == DeletionMode.Permanent
            ? "Permanent — can't be undone."
            : "Goes to Recycle Bin.";

        var confirm = MessageBox.Show(
            $"Clean {plan.AllowedCount} location{(plan.AllowedCount == 1 ? "" : "s")}? " +
            $"Frees up to {ByteSize.Format(plan.TotalBytes)} (files in use are skipped).\n" +
            $"Cache contents removed; folders kept.\n\n{dest}",
            "Confirm cleanup",
            MessageBoxButton.OKCancel,
            mode == DeletionMode.Permanent ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
            return;

        IsScanning = true;
        StatusText = "Cleaning…";
        BeginProgress();
        DeletionResult result;
        try
        {
            var progress = new Progress<(int done, int total, string current)>(p =>
            {
                ReportProgress(p.done, p.total);
                StatusText = p.total > 0
                    ? $"Cleaning… {p.done:N0}/{p.total:N0} — {Shorten(p.current)}"
                    : "Cleaning…";
            });
            result = await Task.Run(() => engine.Execute(plan, mode, progress));
        }
        finally
        {
            EndProgress();
            IsScanning = false;
        }

        var inUse = result.FilesInUse;
        var errored = result.FilesErrored;
        var note = "";
        if (inUse > 0)
            note += $" · {inUse} file{(inUse == 1 ? "" : "s")} in use, skipped";
        if (errored > 0)
            note += $" · {errored} couldn't be removed";
        StatusText = $"Freed {ByteSize.Format(result.BytesReclaimed)}" + note +
                     (mode == DeletionMode.RecycleBin ? " (Recycle Bin)" : " (permanent)");

        // Refresh in-memory instead of re-walking the disk. Only fully clear a
        // folder's children in the view if everything in it was actually removed;
        // if some files were in use or errored, re-scan that folder's accounting
        // by leaving its nodes in place (a later full re-scan will reconcile).
        foreach (var outcome in result.Outcomes)
        {
            if (outcome.Succeeded && outcome.FilesInUse == 0 && outcome.FilesErrored == 0)
                outcome.Finding.Node.ClearChildrenFromTree();
        }
        RefreshAfterPrune();
    }

    /// <summary>Lightweight post-deletion refresh: recompute the summary, the
    /// visible list/treemap, and the cleanup analysis from the now-pruned
    /// in-memory tree — without a disk rescan.</summary>
    private void RefreshAfterPrune()
    {
        if (_scannedRoot is not null)
        {
            SummaryText =
                $"{ByteSize.Format(_scannedRoot.SizeBytes)} in {_scannedRoot.FileCount:N0} files, " +
                $"{_scannedRoot.DirectoryCount:N0} folders";
        }

        // Rebuild the focused views and (in cleanup mode) re-run analysis so the
        // cleaned locations drop out.
        RootViewModel?.RefreshSizes();
        if (Mode == AppMode.Cleanup)
            RunCleanupAnalysis();
        else if (View == RightPaneView.LargestItems)
            RebuildFlatList();

        // Nudge the treemap to redraw from the updated tree.
        Raise(nameof(TreemapRoot));
    }

    private void RunCleanupAnalysis()
    {
        CleanupCategories.Clear();
        CleanupSummary = "";
        _reclaimableByPath.Clear();
        if (_scannedRoot is null)
        {
            CleanupSummary = "Run a scan first, then switch to Cleanup to analyze it.";
            return;
        }

        var analyzer = new CleanupAnalyzer(BundledRules.All);
        _cleanupReport = analyzer.Analyze(_scannedRoot);

        foreach (var cat in _cleanupReport.Categories)
        {
            var catVm = new CleanupCategoryViewModel(cat);
            foreach (var f in catVm.Findings)
                f.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CleanupFindingViewModel.IsSelected))
                        RefreshSelectionState();
                };
            CleanupCategories.Add(catVm);
        }

        // Index findings by path so the list/tree can flag reclaimable items.
        foreach (var f in _cleanupReport.AllFindings)
            _reclaimableByPath[f.Path] = f;

        if (_cleanupReport.AllFindings.Count == 0)
        {
            CleanupSummary = "No known reclaimable locations found in this scan.";
        }
        else
        {
            CleanupSummary =
                $"{ByteSize.Format(_cleanupReport.SafelyReclaimableBytes)} safely reclaimable" +
                $" · {ByteSize.Format(_cleanupReport.TotalReclaimableBytes)} total across " +
                $"{_cleanupReport.AllFindings.Count} location" +
                (_cleanupReport.AllFindings.Count == 1 ? "" : "s");
        }

        // If the cleanup list view is showing, refresh it to pick up flags.
        if (View == RightPaneView.LargestItems)
            RebuildFlatList();
    }

    /// <summary>Reclaimable findings keyed by full path, populated by analysis.
    /// Empty in Storage mode. Used to flag items in the list and tree.</summary>
    private readonly Dictionary<string, CleanupFinding> _reclaimableByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Look up a reclaimable finding for a path, or null. Used by row VMs.</summary>
    public CleanupFinding? ReclaimableFor(string fullPath) =>
        _mode == AppMode.Cleanup && _reclaimableByPath.TryGetValue(fullPath, out var f) ? f : null;


    private void RebuildFlatList()
    {
        FlatItems.Clear();
        var focus = _treemapRoot ?? _scannedRoot;
        if (focus is null)
            return;

        var items = FlatList.Largest(focus, _flatKind, limit: 1000);
        foreach (var node in items)
            FlatItems.Add(new FlatItemViewModel(node, FindReclaimable(node)));
    }

    /// <summary>A node is reclaimable if it (or an ancestor up to the scan root)
    /// matched a cleanup rule. Returns the nearest matching finding, or null.</summary>
    private CleanupFinding? FindReclaimable(FileSystemNode node)
    {
        if (_mode != AppMode.Cleanup || _reclaimableByPath.Count == 0)
            return null;

        for (var n = node; n is not null; n = n.Parent)
        {
            if (_reclaimableByPath.TryGetValue(n.FullPath, out var f))
                return f;
            if (_scannedRoot is not null && ReferenceEquals(n, _scannedRoot))
                break;
        }
        return null;
    }

    private static string Shorten(string path, int max = 70) =>
        path.Length <= max ? path : "…" + path[^(max - 1)..];
}
