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

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };
        Grid.SetRow(scroll, 1);

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
            foreach (var group in _report.Groups)
                _list.Children.Add(BuildGroupCard(group));
        }

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

        // One row per file with a Delete button (disabled for the first/kept copy
        // by convention, but the user may delete any — at least one is implicitly
        // kept since we stop offering once only one remains).
        foreach (var file in group.Files)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathText = new TextBlock
            {
                Text = file.FullPath,
                Foreground = text, FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pathText, 0);

            var del = new Button
            {
                Content = "Delete copy",
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Tag = file,
            };
            del.Click += (s, _) => DeleteCopy((FileSystemNode)((Button)s).Tag, stack, group, del);
            Grid.SetColumn(del, 1);

            row.Children.Add(pathText);
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

    private void DeleteCopy(FileSystemNode file, StackPanel groupStack, DuplicateGroup group, Button btn)
    {
        // Count remaining (non-deleted) rows; never let the user remove the last copy.
        var remaining = groupStack.Children.OfType<Grid>().Count(g => g.IsEnabled);
        if (remaining <= 1)
        {
            MessageBox.Show("This is the last remaining copy — keeping it.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!DeletionEngine.CanDeleteFolder(file.FullPath))
        {
            MessageBox.Show("That file is in a protected location and can't be removed here.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ok = MessageBox.Show(
            $"Send this copy to the Recycle Bin?\n\n{file.FullPath}",
            "Delete duplicate", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK)
            return;

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
}
