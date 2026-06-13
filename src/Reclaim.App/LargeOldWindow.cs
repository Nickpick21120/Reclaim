using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Reclaim.Core.Cleanup;
using Reclaim.Core.Formatting;
using Reclaim.Core.LargeOld;
using Reclaim.Core.Scanning;
using Reclaim.App.Services;

namespace Reclaim.App;

/// <summary>
/// Shows files that are both large and not modified in a long time — the
/// forgotten heavy stuff worth reviewing. Deletion reuses the same trust guard
/// as elsewhere (protected system files can't be removed here; system locations
/// warn), and sends to the Recycle Bin.
/// </summary>
public sealed class LargeOldWindow : Window
{
    private readonly LargeOldReport _report;
    private readonly StackPanel _list = new();
    private readonly Action _onChanged;

    public LargeOldWindow(LargeOldReport report, Action onChanged)
    {
        _report = report;
        _onChanged = onChanged;

        Title = "Reclaim — Large & old files";
        Width = 860;
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

        var summary = new TextBlock
        {
            Foreground = text, FontSize = 14, Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Text = _report.Files.Count == 0
                ? "No files matched those size and age thresholds."
                : $"{_report.Files.Count} large, old file(s) · {ByteSize.Format(_report.TotalBytes)} total. "
                  + "These are big files you haven't changed in a long time — often safe to remove or archive, "
                  + "but review each one.",
        };
        Grid.SetRow(summary, 0);

        foreach (var f in _report.Files)
            _list.Children.Add(BuildRow(f, text, dim));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };
        Grid.SetRow(scroll, 1);

        root.Children.Add(summary);
        root.Children.Add(scroll);
        Content = root;
    }

    private Border BuildRow(LargeOldFile f, Brush text, Brush dim)
    {
        var trust = LocationTrustClassifier.Classify(f.Node.FullPath);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        info.Children.Add(new TextBlock
        {
            Text = f.Node.Name,
            Foreground = text, FontWeight = FontWeights.SemiBold, FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = f.Node.FullPath,
            Foreground = dim, FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{ByteSize.Format(f.SizeBytes)} · last modified ~{YearsMonths(f.AgeDays)} ago",
            Foreground = dim, FontSize = 10.5,
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (trust != LocationTrust.Normal)
        {
            var isProtected = trust == LocationTrust.Protected;
            grid.Children.Add(WithColumn(new Border
            {
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(isProtected
                    ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xD9, 0xA4, 0x41)),
                Child = new TextBlock
                {
                    Text = isProtected ? "System — protected" : "System location",
                    FontSize = 10, Foreground = Brushes.White,
                },
            }, 1));
        }

        var del = new Button
        {
            Content = trust == LocationTrust.Protected ? "Protected" : "Delete…",
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = trust != LocationTrust.Protected,
            Tag = f.Node,
        };
        if (trust != LocationTrust.Protected)
            del.Click += (_, _) => DeleteOne(f.Node, trust, del);
        Grid.SetColumn(del, 2);
        grid.Children.Add(del);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private static FrameworkElement WithColumn(FrameworkElement el, int col)
    {
        Grid.SetColumn(el, col);
        return el;
    }

    private static string YearsMonths(int days)
    {
        if (days >= 365)
        {
            var years = days / 365;
            return years == 1 ? "1 year" : $"{years} years";
        }
        var months = Math.Max(1, days / 30);
        return months == 1 ? "1 month" : $"{months} months";
    }

    private void DeleteOne(FileSystemNode node, LocationTrust trust, Button btn)
    {
        if (trust == LocationTrust.Protected ||
            LocationTrustClassifier.Classify(node.FullPath) == LocationTrust.Protected ||
            !DeletionEngine.CanDeleteFolder(node.FullPath))
        {
            MessageBox.Show("That file is in a protected system location and can't be removed here.",
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prompt = trust == LocationTrust.System
            ? "This file is in a system location:\n\n" + node.FullPath +
              "\n\nIt may be needed by Windows or an installed program. Send it to the Recycle Bin anyway?"
            : $"Send this file to the Recycle Bin?\n\n{node.FullPath}\n\n({ByteSize.Format(node.SizeBytes)})";

        if (MessageBox.Show(prompt, "Delete file",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        try
        {
            var outcome = new ShellFileRemover().Remove(node.FullPath, DeletionMode.RecycleBin);
            if (outcome != RemovalOutcome.Removed)
            {
                MessageBox.Show(
                    outcome == RemovalOutcome.InUse
                        ? "That file is in use by another program and couldn't be removed right now."
                        : "That file couldn't be removed.",
                    "Reclaim", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            node.RemoveFromTree();
            if (btn.Parent is Grid g && g.Parent is Border b)
            {
                b.Opacity = 0.4;
                btn.IsEnabled = false;
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
