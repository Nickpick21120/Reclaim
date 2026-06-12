using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Reclaim.App;

/// <summary>
/// A lighthearted "junk popper" easter-egg game, launched by clicking the RC
/// logo. Junk files pop up around the board; click them to reclaim space before
/// they vanish. Entirely cosmetic and self-contained — it never touches the
/// scanner, the cleanup engine, or any real file. Pure fun.
/// </summary>
public sealed class JunkPopperWindow : Window
{
    private readonly Canvas _board = new();
    private readonly TextBlock _scoreText = new();
    private readonly TextBlock _timeText = new();
    private readonly TextBlock _centerText = new();
    private readonly DispatcherTimer _spawnTimer = new();
    private readonly DispatcherTimer _clockTimer = new();
    private readonly Random _rng = new();
    private readonly List<Mole> _moles = [];
    private readonly ChiptunePlayer _music = new();

    private int _score;
    private double _timeLeft = 30.0;
    private bool _running;
    private long _bytesReclaimed;

    // Tuning — easy to tweak after playtesting.
    private const double GameSeconds = 30.0;
    private static readonly string[] JunkLabels =
        ["cache.tmp", "thumbs.db", "log.old", ".DS_Store", "temp.dat", "~build", "crash.dmp", "stale.bak"];

    public JunkPopperWindow()
    {
        Title = "Reclaim — Junk Popper";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x07));

        BuildUi();

        _spawnTimer.Interval = TimeSpan.FromMilliseconds(650);
        _spawnTimer.Tick += (_, _) => SpawnMole();
        _clockTimer.Interval = TimeSpan.FromMilliseconds(100);
        _clockTimer.Tick += (_, _) => Tick();

        ShowStartScreen();
    }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // HUD
        var hud = new Grid { Margin = new Thickness(16, 12, 16, 8) };
        hud.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hud.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hud.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var accent = new SolidColorBrush(Color.FromRgb(0x2D, 0x6B, 0xFF));
        var dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8));

        _scoreText.Foreground = accent;
        _scoreText.FontWeight = FontWeights.Bold;
        _scoreText.FontSize = 18;
        _scoreText.Text = "Reclaimed: 0";
        _scoreText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_scoreText, 0);

        // Volume control (center): a small speaker label + slider, defaulting to 30%.
        var volPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volPanel.Children.Add(new TextBlock
        {
            Text = "\uE767", // speaker glyph
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var volSlider = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0.30,       // 30% default
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Music volume",
        };
        volSlider.ValueChanged += (_, e) => _music.Volume = e.NewValue;
        volPanel.Children.Add(volSlider);
        Grid.SetColumn(volPanel, 1);

        _timeText.Foreground = dim;
        _timeText.FontSize = 16;
        _timeText.HorizontalAlignment = HorizontalAlignment.Right;
        _timeText.VerticalAlignment = VerticalAlignment.Center;
        _timeText.Text = "30.0s";
        Grid.SetColumn(_timeText, 2);

        hud.Children.Add(_scoreText);
        hud.Children.Add(volPanel);
        hud.Children.Add(_timeText);
        Grid.SetRow(hud, 0);

        // Board
        _board.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
        _board.ClipToBounds = true;
        _board.MouseLeftButtonDown += OnBoardClick;
        Grid.SetRow(_board, 1);

        // Center message (start / game over)
        _centerText.Foreground = Brushes.White;
        _centerText.FontSize = 22;
        _centerText.TextAlignment = TextAlignment.Center;
        _centerText.HorizontalAlignment = HorizontalAlignment.Center;
        _centerText.VerticalAlignment = VerticalAlignment.Center;
        _centerText.TextWrapping = TextWrapping.Wrap;
        var overlayGrid = new Grid();
        overlayGrid.Children.Add(_board);
        overlayGrid.Children.Add(_centerText);
        Grid.SetRow(overlayGrid, 1);

        root.Children.Add(hud);
        root.Children.Add(overlayGrid);
        Content = root;
    }

    private void ShowStartScreen()
    {
        _centerText.Text = "Junk Popper\n\nClick the junk files to reclaim space\nbefore they disappear!\n\nClick anywhere to start";
        _centerText.Visibility = Visibility.Visible;
        _board.MouseLeftButtonDown -= OnBoardClick;
        _board.MouseLeftButtonDown += StartOnClick;
    }

    private void StartOnClick(object sender, MouseButtonEventArgs e)
    {
        _board.MouseLeftButtonDown -= StartOnClick;
        _board.MouseLeftButtonDown += OnBoardClick;
        StartGame();
    }

    private void StartGame()
    {
        _score = 0;
        _bytesReclaimed = 0;
        _timeLeft = GameSeconds;
        _running = true;
        _centerText.Visibility = Visibility.Collapsed;
        ClearMoles();
        UpdateHud();
        _music.Start();
        _spawnTimer.Start();
        _clockTimer.Start();
    }

    private void Tick()
    {
        if (!_running)
            return;

        _timeLeft -= 0.1;
        if (_timeLeft <= 0)
        {
            EndGame();
            return;
        }

        // Age moles; remove any that have outlived their lifetime.
        var now = DateTime.UtcNow;
        for (var i = _moles.Count - 1; i >= 0; i--)
        {
            if (now >= _moles[i].Expires)
            {
                _board.Children.Remove(_moles[i].Visual);
                _moles.RemoveAt(i);
            }
        }
        UpdateHud();
    }

    private void SpawnMole()
    {
        if (!_running)
            return;

        // Keep the board from getting too crowded.
        if (_moles.Count >= 6)
            return;

        var w = Math.Max(120, _board.ActualWidth);
        var h = Math.Max(120, _board.ActualHeight);
        const double size = 84;

        var x = _rng.NextDouble() * (w - size);
        var y = _rng.NextDouble() * (h - size);

        var label = JunkLabels[_rng.Next(JunkLabels.Length)];
        var bytes = (long)_rng.Next(50, 5000) * 1024; // pretend size

        var visual = BuildMoleVisual(label, size);
        Canvas.SetLeft(visual, x);
        Canvas.SetTop(visual, y);
        _board.Children.Add(visual);

        _moles.Add(new Mole
        {
            Visual = visual,
            Expires = DateTime.UtcNow.AddMilliseconds(_rng.Next(900, 1600)),
            Bytes = bytes,
        });
    }

    private Border BuildMoleVisual(string label, double size)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center,
                                     VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = "\uE7C3", // page/file glyph
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 30,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x41)), // gold "junk"
            Child = stack,
            Cursor = Cursors.Hand,
        };
    }

    private void OnBoardClick(object sender, MouseButtonEventArgs e)
    {
        if (!_running)
            return;

        var pos = e.GetPosition(_board);
        // Hit-test moles top-most first.
        for (var i = _moles.Count - 1; i >= 0; i--)
        {
            var m = _moles[i];
            var left = Canvas.GetLeft(m.Visual);
            var top = Canvas.GetTop(m.Visual);
            if (pos.X >= left && pos.X <= left + m.Visual.Width &&
                pos.Y >= top && pos.Y <= top + m.Visual.Height)
            {
                _score++;
                _bytesReclaimed += m.Bytes;
                _board.Children.Remove(m.Visual);
                _moles.RemoveAt(i);
                UpdateHud();
                return;
            }
        }
    }

    private void EndGame()
    {
        _running = false;
        _spawnTimer.Stop();
        _clockTimer.Stop();
        _music.Stop();
        ClearMoles();
        _centerText.Text =
            $"Time's up!\n\nReclaimed {_score} junk files\n({Reclaim.Core.Formatting.ByteSize.Format(_bytesReclaimed)} of imaginary space)\n\nClick to play again";
        _centerText.Visibility = Visibility.Visible;
        _board.MouseLeftButtonDown -= OnBoardClick;
        _board.MouseLeftButtonDown += StartOnClick;
    }

    private void ClearMoles()
    {
        foreach (var m in _moles)
            _board.Children.Remove(m.Visual);
        _moles.Clear();
    }

    private void UpdateHud()
    {
        _scoreText.Text = $"Reclaimed: {_score}  ·  {Reclaim.Core.Formatting.ByteSize.Format(_bytesReclaimed)}";
        _timeText.Text = $"{Math.Max(0, _timeLeft):0.0}s";
    }

    protected override void OnClosed(EventArgs e)
    {
        _spawnTimer.Stop();
        _clockTimer.Stop();
        _music.Dispose();
        base.OnClosed(e);
    }

    private sealed class Mole
    {
        public required Border Visual { get; init; }
        public required DateTime Expires { get; init; }
        public required long Bytes { get; init; }
    }
}
