using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Reclaim.App.ViewModels;

namespace Reclaim.App;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Treemap.DrillRequested += node => Vm.TreemapRoot = node;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        StateChanged += (_, _) => UpdateMaxButtonGlyph();
        Vm.DuplicatesReady += report =>
        {
            var win = new DuplicatesWindow(report, () => Vm.NotifyExternalChange()) { Owner = this };
            win.Show();
        };
        Vm.ChooseDuplicateScopeRequested += () =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder to limit the duplicate scan" };
            if (dialog.ShowDialog(this) == true)
                Vm.SetDuplicateScope(dialog.FolderName);
        };
        Vm.LargeOldReady += report =>
        {
            var win = new LargeOldWindow(report, () => Vm.NotifyExternalChange()) { Owner = this };
            win.Show();
        };
        Vm.ExportScanRequested += () =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export scan",
                Filter = "CSV (spreadsheet)|*.csv|JSON (nested tree)|*.json",
                FileName = "reclaim-scan",
                DefaultExt = ".csv",
            };
            if (dialog.ShowDialog(this) == true)
                Vm.WriteExport(dialog.FileName);
        };

        // Crash detection: a crash report file exists only when the previous run
        // caught an unhandled exception. That's the reliable signal — far more so
        // than an exit-marker, which false-positives whenever the exit handler
        // doesn't run (debugger stop, host kill, etc.).
        Loaded += (_, _) => CheckForPreviousCrash();
    }

    private void CheckForPreviousCrash()
    {
        var report = Services.Diagnostics.ReadLastCrash();
        if (report is null)
            return; // no captured crash last run — normal launch

        // Clear first so we only ever prompt once for a given crash.
        Services.Diagnostics.ClearLastCrash();

        var win = new CrashReportWindow(report) { Owner = this };
        win.ShowDialog();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(Vm.Settings) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Saved)
            Vm.ApplySettings();
    }

    private void UpdateMaxButtonGlyph()
    {
        // Segoe MDL2: E922 = maximize, E923 = restore.
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        // WindowChrome maximizes slightly past the work area, clipping edges;
        // a small inset when maximized keeps all content on-screen.
        RootBorder.Margin = WindowState == WindowState.Maximized
            ? new Thickness(7) : new Thickness(0);
    }

    /// <summary>Ask the Desktop Window Manager to render this window's native
    /// title bar in dark mode, so it matches the app's dark theme instead of
    /// showing the bright default chrome. Best-effort: silently ignored on
    /// Windows versions that don't support the attribute.</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (1903+); 19 on older builds.
            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));

            // Tint the title bar to the app's panel color (Win11 22000+).
            int caption = 0x00120D0B; // COLORREF 0x00BBGGRR ≈ #0B0D12
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        }
        catch
        {
            // Non-fatal: the window simply keeps the default title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder or drive to scan",
        };

        if (dialog.ShowDialog(this) == true)
            Vm.TargetPath = dialog.FolderName;
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is NodeViewModel vm)
        {
            // Show the "what is this?" info for any selected item.
            Vm.DescribeSelection(vm.Node);
            // Selecting a directory also focuses the treemap on it.
            if (vm.IsDirectory)
                Vm.TreemapRoot = vm.Node;
        }
        else
        {
            Vm.DescribeSelection(null);
        }
    }

    private void OnFlatItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid { SelectedItem: FlatItemViewModel item })
            Vm.DescribeSelection(item.Node);
    }

    private void OnFlatItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid { SelectedItem: FlatItemViewModel item })
            return;

        if (item.IsDirectory)
        {
            // Drill the shared focus into this folder, staying in the list so
            // the user can keep browsing downward; breadcrumb + treemap follow.
            Vm.FocusOn(item.Node);
        }
        else if (item.Node.Parent is { } parent)
        {
            // A file has nothing to drill into — show it in spatial context.
            Vm.FocusOn(parent);
            Vm.View = ViewModels.RightPaneView.Treemap;
        }
    }

    /// <summary>Show the descending-sort arrow on the Size column to advertise
    /// that headers are sortable and to match the list's default order.</summary>
    // The slider's Value is bound live (so the label tracks the thumb), but the
    // expensive treemap recompute is deferred until the gesture settles: on
    // drag release, or — for keyboard/click moves that don't drag — on a short
    // debounce timer so rapid arrow presses coalesce into one rebuild.
    private bool _sliderDragging;
    private DispatcherTimer? _sliderSettle;

    private void OnSliderDragStarted(object sender, DragStartedEventArgs e) =>
        _sliderDragging = true;

    private void OnSliderCommit(object sender, DragCompletedEventArgs e)
    {
        _sliderDragging = false;
        Vm.CommitMinSize();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderDragging)
            return; // mid-drag: label updates via binding, treemap waits

        // Keyboard / click-to-point: debounce so a burst of changes rebuilds once.
        _sliderSettle ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _sliderSettle.Tick -= OnSliderSettled;
        _sliderSettle.Tick += OnSliderSettled;
        _sliderSettle.Stop();
        _sliderSettle.Start();
    }

    private void OnSliderSettled(object? sender, EventArgs e)
    {
        _sliderSettle?.Stop();
        Vm.CommitMinSize();
    }

    private void OnFindingClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.CleanupFindingViewModel vm })
            Vm.TreemapRoot = vm.Finding.Node;
    }

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ViewModels.BreadcrumbItem item } && !item.IsLast)
            Vm.FocusOn(item.Node);
    }

    // Easter egg: double-click the RC logo to launch a random minigame.
    private Window? _miniGame;
    private readonly Random _gameRng = new();
    private void OnLogoClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        // Hidden developer trigger: Ctrl+Shift+double-click forces a test crash so
        // the crash-report flow can be verified end to end. Gated behind a modifier
        // combo a normal user won't hit, plus a confirmation, so it's safe to ship.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift))
            == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var confirm = MessageBox.Show(
                "Developer test: simulate an unhandled-exception crash now?\n\n" +
                "This verifies the crash-report flow for app errors (unhandled exceptions). " +
                "The app will close; on next launch you should see the crash report dialog.\n\n" +
                "Note: this is the kind of crash Reclaim can detect. Hard kills (Task Manager " +
                "\u201cEnd task\u201d), power loss, or native runtime crashes cannot be captured by " +
                "any in-process reporter and won't produce a report.",
                "Trigger test crash", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.OK)
            {
                // Write a crash report just like a real fatal error would, then shut
                // down — so the next launch exercises the report dialog. (Throwing
                // here would be caught by the dispatcher handler, which deliberately
                // keeps the app alive, so it wouldn't reproduce a fatal-crash launch.)
                Services.Diagnostics.WriteCrash("Manual test",
                    new InvalidOperationException(
                        "Deliberate test crash triggered from the RC logo (Ctrl+Shift+double-click)."));
                Application.Current.Shutdown();
            }
            return;
        }

        if (_miniGame is { IsLoaded: true })
        {
            _miniGame.Activate();
            return;
        }

        // Randomly pick one of the two games.
        _miniGame = _gameRng.Next(2) == 0
            ? new JunkPopperWindow { Owner = this }
            : new SpaceInvadersWindow { Owner = this };
        _miniGame.Closed += (_, _) => _miniGame = null;
        _miniGame.Show();
    }
}
