using System.Windows;
using System.Windows.Controls;
using Reclaim.App.Services;

namespace Reclaim.App;

/// <summary>
/// A small preferences dialog. Currently two settings: the default deletion mode
/// (Recycle Bin vs permanent) and whether to remember the last scanned folder.
/// Changes are saved when the dialog is closed with Save. Uses the shared Theme
/// palette so it stays visually consistent with the rest of the app.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CheckBox _permanentDelete;
    private readonly CheckBox _rememberFolder;
    private readonly CheckBox _mftScan;

    /// <summary>Set to true if the user saved changes, so the caller can apply them.</summary>
    public bool Saved { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;

        Title = "Reclaim — Settings";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Theme.BgBrush;

        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(new TextBlock
        {
            Text = "Settings",
            Foreground = Theme.TextBrush, FontSize = 17, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // --- Deletion mode ---
        root.Children.Add(SectionLabel("Deletion"));
        _permanentDelete = new CheckBox
        {
            Content = "Delete permanently by default (skip the Recycle Bin)",
            Foreground = Theme.TextBrush,
            IsChecked = _settings.DefaultPermanentDelete,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _permanentDelete.Checked += (_, _) => UpdateWarning();
        _permanentDelete.Unchecked += (_, _) => UpdateWarning();
        root.Children.Add(_permanentDelete);

        _warning = new TextBlock
        {
            Foreground = Theme.WarnBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 4, 0, 0),
            Text = "Warning: permanently deleted files cannot be recovered. "
                 + "The Recycle Bin is recommended so mistakes can be undone.",
        };
        root.Children.Add(_warning);

        // --- Scanning ---
        root.Children.Add(SectionLabel("Scanning"));
        _rememberFolder = new CheckBox
        {
            Content = "Remember the last scanned folder and pre-fill it on launch",
            Foreground = Theme.TextBrush,
            IsChecked = _settings.RememberLastFolder,
            Margin = new Thickness(0, 4, 0, 0),
        };
        root.Children.Add(_rememberFolder);

        // --- Experimental ---
        root.Children.Add(SectionLabel("Experimental"));
        _mftScan = new CheckBox
        {
            Content = "Fast MFT scan for whole NTFS drives (requires admin)",
            Foreground = Theme.TextBrush,
            IsChecked = _settings.ExperimentalMftScan,
            Margin = new Thickness(0, 4, 0, 0),
        };
        root.Children.Add(_mftScan);
<<<<<<< Updated upstream
=======
        var mftHelp = "Reads the NTFS Master File Table directly for very fast whole-drive "
                    + "scans (typically a few seconds). Applies when scanning a drive root "
                    + "as administrator; otherwise the normal scanner is used automatically.";
        if (!Services.Elevation.IsElevated())
            mftHelp += "  You're not running as administrator right now — enabling this "
                     + "will offer to restart Reclaim as admin.";
>>>>>>> Stashed changes
        root.Children.Add(new TextBlock
        {
            Foreground = Theme.TextDimBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 4, 0, 0),
<<<<<<< Updated upstream
            Text = "Experimental: reads the NTFS Master File Table directly for very fast "
                 + "whole-drive scans. Only applies when scanning a drive root as administrator; "
                 + "falls back to the normal scanner otherwise.",
=======
            Text = mftHelp,
>>>>>>> Stashed changes
        });

        // --- Buttons ---
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var save = new Button
        {
            Content = "Save", Padding = new Thickness(16, 6, 16, 6),
            Background = Theme.AccentBrush, Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
        };
        save.Click += (_, _) => DoSave();
        var cancel = new Button
        {
            Content = "Cancel", Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        UpdateWarning();
    }

    private readonly TextBlock _warning;

    private void UpdateWarning() =>
        _warning.Visibility = _permanentDelete.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = Theme.TextDimBrush, FontSize = 11, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 2),
    };

    private void DoSave()
    {
<<<<<<< Updated upstream
=======
        var mftNewlyEnabled = _mftScan.IsChecked == true && !_settings.ExperimentalMftScan;

>>>>>>> Stashed changes
        _settings.DefaultPermanentDelete = _permanentDelete.IsChecked == true;
        _settings.RememberLastFolder = _rememberFolder.IsChecked == true;
        _settings.ExperimentalMftScan = _mftScan.IsChecked == true;
        // If the user turned off "remember", clear any stored folder too.
        if (!_settings.RememberLastFolder)
            _settings.LastFolder = "";
        _settings.Save();
        Saved = true;
<<<<<<< Updated upstream
=======

        // MFT scanning reads the raw NTFS volume, which requires administrator
        // rights. If the user just enabled it but isn't elevated, offer to restart
        // as admin now — an explicit, informed choice (it triggers a UAC prompt).
        // Declining is fine: the setting stays on and takes effect next time the app
        // runs elevated; until then scans transparently use the normal scanner.
        if (mftNewlyEnabled && !Services.Elevation.IsElevated())
        {
            var choice = MessageBox.Show(
                "Fast MFT scanning reads the raw NTFS volume directly, which requires "
                + "running Reclaim as administrator.\n\n"
                + "Restart Reclaim as administrator now to use it?\n\n"
                + "You can also say No — the setting is saved and will take effect the "
                + "next time you run Reclaim as administrator. Until then, scans use the "
                + "normal scanner automatically.",
                "Restart as administrator?",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes)
            {
                // Relaunch elevated (UAC). If the user declines UAC or it fails, we
                // stay running un-elevated; the setting remains saved either way.
                if (!Services.Elevation.RestartElevated())
                {
                    MessageBox.Show(
                        "Reclaim wasn't restarted as administrator. The MFT setting is "
                        + "saved and will apply once you run Reclaim as administrator.",
                        "Not restarted", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                // If RestartElevated() succeeded it already shut this instance down.
            }
        }

>>>>>>> Stashed changes
        Close();
    }
}
