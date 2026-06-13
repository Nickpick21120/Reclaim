using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Reclaim.Core.Cleanup;
using Reclaim.Core.Duplicates;
using Reclaim.Core.Formatting;
using Reclaim.Core.Scanning;
using Reclaim.App.Services;

namespace Reclaim.App;

/// <summary>
/// Shows duplicate-file groups found in the current scan. For each group the
/// user can keep one copy and send the rest to the Recycle Bin. Self-contained
/// window so it doesn't disturb the main layout. Deletion reuses the safe
/// ShellFileRemover + protected-path guard.
/// </summary>
public sealed class DuplicatesWindow : Window
{
    private readonly DuplicateReport _report;
    private readonly StackPanel _list = new();
    private readonly TextBlock _summary = new();
    private readonly Action _onChanged;

    public DuplicatesWindow(DuplicateReport report, Action onChanged)
    {
        _report = report;
        _onChanged = onChanged;

        Title = "Reclaim — Duplicate files";
        Width = 820;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x07));

        Build();
    }

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var text = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEC));
        var dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8));

        _summary.Foreground = text;
        _summary.FontSize = 14;
        _summary.Margin = new Thickness(0, 0, 0, 12);
        _summary.TextWrapping = TextWrapping.Wrap;
        UpdateSummary();
        Grid.SetRow(_summary, 0);

        if (_report.Groups.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No duplicate files found in this scan. ",
                Foreground = dim, FontSize = 13, Margin = new Thickness(0, 8, 0, 0),
            });
        }
        else
        {
            // The report is already capped by the finder, but cap again for the UI
            // as defense-in-depth so we never build a pathological number of cards.
            const int uiMax = 200;
            var shown = Math.Min(_report.Groups.Count, uiMax);
            for (var i = 0; i < shown; i++)
                _list.Children.Add(BuildGroupCard(_report.Groups[i]));

            if (_report.Groups.Count > uiMax)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = $"Showing the {uiMax} largest groups of {_report.TotalGroupsFound}. "
                         + "Clean these first, then scan again to see more.",
                    Foreground = dim, FontSize = 11.5, Margin = new Thickness(2, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };
        Grid.SetRow(scroll, 1);

        root.Children.Add(_summary);
        root.Children.Add(scroll);
        Content = root;
    }

    private void UpdateSummary()
    {
        _summary.Text = _report.Groups.Count == 0
            ? "Scanned for duplicates — none found."
            : $"Found {_report.Groups.Count} group(s) of identical files. " +
              $"Removing the extra copies would reclaim {ByteSize.Format(_report.TotalReclaimableBytes)}. " +
              "Each group keeps one copy; pick which others to remove.";
    }

    private Border BuildGroupCard(DuplicateGroup group)
    {
        var panelBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A));
        var text = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEC));
        var dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8));
        var accent = new SolidColorBrush(Color.FromRgb(0x2D, 0x6B, 0xFF));

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{group.Files.Count} copies · {ByteSize.Format(group.FileSizeBytes)} each · " +
                   $"reclaim {ByteSize.Format(group.ReclaimableBytes)}",
            Foreground = accent, FontWeight = FontWeights.SemiBold, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var file in group.Files)
        {
            var trust = LocationTrustClassifier.Classify(file.FullPath);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathText = new TextBlock
            {
                Text = file.FullPath,
                Foreground = text, FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pathText, 0);
            row.Children.Add(pathText);

            // A trust badge for system/protected locations so the user is never
            // unaware of what they're about to touch.
            if (trust != LocationTrust.Normal)
            {
                var isProtected = trust == LocationTrust.Protected;
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(isProtected
                        ? Color.FromRgb(0xC4, 0x2B, 0x1C)   // red: protected
                        : Color.FromRgb(0xD9, 0xA4, 0x41)), // gold: warn
                    Child = new TextBlock
                    {
                        Text = isProtected ? "System — protected" : "System location",
                        FontSize = 10, Foreground = Brushes.White,
                    },
                };
                Grid.SetColumn(badge, 1);
                row.Children.Add(badge);
            }

            var del = new Button
            {
                Content = trust == LocationTrust.Protected ? "Protected" : "Delete copy",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Tag = file,
                // Hard-block deletion of protected system files from here.
                IsEnabled = trust != LocationTrust.Protected,
                ToolTip = trust == LocationTrust.Protected
                    ? "This file is in a protected system location and can't be deleted here."
                    : trust == LocationTrust.System
                        ? "This is in a system location — make sure it isn't needed before deleting."
                        : null,
            };
            if (trust != LocationTrust.Protected)
                del.Click += (s, _) => DeleteCopy((FileSystemNode)((Button)s).Tag, stack, group, del, trust);
            Grid.SetColumn(del, 2);
            row.Children.Add(del);

            stack.Children.Add(row);
        }

        return new Border
        {
            Background = panelBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    private void DeleteCopy(FileSystemNode file, StackPanel groupStack, DuplicateGroup group, Button btn,
        LocationTrust trust)
    {
        // GROUND-TRUTH SAFETY CHECK: verify, against the real filesystem right now,
        // that at least one OTHER copy in this group still exists. The scan tree can
        // be stale (files moved/deleted/cleaned since the scan), so counting UI rows
        // isn't enough — we must confirm on disk before this destructive act, or we
        // risk deleting the only surviving copy.
        var file1Exists = SafeExists(file.FullPath);
        var otherRealCopies = group.Files
            .Where(f => !PathsEqual(f.FullPath, file.FullPath))
            .Count(f => SafeExists(f.FullPath));

        if (otherRealCopies == 0)
        {
            MessageBox.Show(
                file1Exists
                    ? "The other copies in this group no longer exist on disk, so this is now the only copy left — keeping it.\n\n" +
                      "(The scan may be out of date. Re-scan to refresh.)"
                    : "This file no longer exists on disk — it may have already been moved or deleted.\n\n" +
                      "Re-scan to refresh the list.",
                "Reclaim — nothing safe to delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Reflect reality in the row.
            if (btn.Parent is Grid r)
            {
                r.IsEnabled = false;
                r.Opacity = 0.4;
                btn.Content = file1Exists ? "Only copy" : "Missing";
            }
            return;
        }

        // Defense in depth: never delete a protected-location file here, even if a
        // button somehow got through.
        if (trust == LocationTrust.Protected ||
            LocationTrustClassifier.Classify(file.FullPath) == LocationTrust.Protected ||
            !DeletionEngine.CanDeleteFolder(file.FullPath))
        {
            MessageBox.Show("That file is in a protected system location and can't be removed here.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // System-location files get a sterner, explicit warning — duplicates here
        // are usually deliberate (shared runtimes/components).
        if (trust == LocationTrust.System)
        {
            var warn = MessageBox.Show(
                "This file is in a system location:\n\n" + file.FullPath +
                "\n\nCopies here are often kept on purpose by Windows or an installed program. " +
                "Deleting it could affect that software. Are you sure you want to send it to the Recycle Bin?",
                "System file — are you sure?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (warn != MessageBoxResult.OK)
                return;
        }
        else
        {
            var ok = MessageBox.Show(
                $"Send this copy to the Recycle Bin?\n\n{file.FullPath}",
                "Delete duplicate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK)
                return;
        }

        // Final ground-truth check immediately before deleting: the target must
        // still exist (it could have vanished since the list was built), and at
        // least one other real copy must remain (re-checked, in case disk changed
        // during the confirmation dialog).
        if (!SafeExists(file.FullPath))
        {
            MessageBox.Show("That file no longer exists on disk. Re-scan to refresh the list.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (btn.Parent is Grid gm) { gm.IsEnabled = false; gm.Opacity = 0.4; btn.Content = "Missing"; }
            return;
        }
        var stillOthers = group.Files
            .Where(f => !PathsEqual(f.FullPath, file.FullPath))
            .Count(f => SafeExists(f.FullPath));
        if (stillOthers == 0)
        {
            MessageBox.Show("This is now the only remaining copy — keeping it.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var outcome = new ShellFileRemover().Remove(file.FullPath, DeletionMode.RecycleBin);
            if (outcome != RemovalOutcome.Removed)
            {
                MessageBox.Show(
                    outcome == RemovalOutcome.InUse
                        ? "That file is in use by another program and couldn't be removed right now."
                        : "That file couldn't be removed.",
                    "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            file.RemoveFromTree();
            // Grey out and disable the deleted row.
            if (btn.Parent is Grid row)
            {
                row.IsEnabled = false;
                row.Opacity = 0.4;
                btn.Content = "Removed";
            }
            _onChanged();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't delete: {ex.Message}", "Reclaim",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Whether a file currently exists on disk, never throwing.</summary>
    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a?.TrimEnd('\\'), b?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
