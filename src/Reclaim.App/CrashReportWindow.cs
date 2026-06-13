using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Reclaim.App.Services;

namespace Reclaim.App;

/// <summary>
/// Shown on the next launch after an unclean shutdown. Displays the full crash
/// diagnostics so the user can see exactly what would be shared, then lets them
/// send it to the developer (opens a pre-filled GitHub issue in the browser),
/// copy it, or dismiss. Nothing leaves the machine without the user's action.
/// </summary>
public sealed class CrashReportWindow : Window
{
    private readonly string _report;

    public CrashReportWindow(string report)
    {
        _report = report;

        Title = "Reclaim — crash report";
        Width = 640;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x07));

        var text = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEC));
        var dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8));
        var accent = new SolidColorBrush(Color.FromRgb(0x2D, 0x6B, 0xFF));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Reclaim didn't close properly last time",
            Foreground = text, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        };
        Grid.SetRow(heading, 0);

        var blurb = new TextBlock
        {
            Text = "Below is the diagnostic report. Nothing is sent automatically. "
                 + "You can review it, then send it to the developer (it opens a pre-filled "
                 + "report in your browser for you to submit), or copy it to share yourself.",
            Foreground = dim, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(blurb, 1);

        var box = new TextBox
        {
            Text = _report,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x16)),
            Foreground = text,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3A)),
            Padding = new Thickness(8),
        };
        Grid.SetRow(box, 2);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var send = new Button
        {
            Content = "Send diagnostics to developer",
            Padding = new Thickness(14, 6, 14, 6),
            Background = accent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        send.Click += (_, _) => SendToDeveloper();

        var copy = new Button
        {
            Content = "Copy",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_report); } catch { /* ignore */ }
        };

        var dismiss = new Button
        {
            Content = "Dismiss",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        dismiss.Click += (_, _) => Close();

        buttons.Children.Add(send);
        buttons.Children.Add(copy);
        buttons.Children.Add(dismiss);
        Grid.SetRow(buttons, 3);

        root.Children.Add(heading);
        root.Children.Add(blurb);
        root.Children.Add(box);
        root.Children.Add(buttons);
        Content = root;
    }

    private void SendToDeveloper()
    {
        try
        {
            var url = Diagnostics.BuildIssueUrl(_report);
            // Opens the user's default browser at a pre-filled issue form. The user
            // still has to click submit — nothing is posted automatically.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Couldn't open the browser. You can use Copy instead and paste the report manually.\n\n" + ex.Message,
                "Reclaim", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
