using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Reclaim.App;

/// <summary>
/// A small Space-Invaders-style game: move the "RC" ship left/right with the
/// mouse, click to fire, clear descending rows of junk files before they reach
/// the bottom. Fully self-contained and cosmetic — touches no real files.
/// </summary>
public sealed class SpaceInvadersWindow : Window
{
    private readonly Canvas _board = new();
    private readonly TextBlock _scoreText = new();
    private readonly TextBlock _centerText = new();
    private readonly DispatcherTimer _timer = new();
    private readonly ChiptunePlayer _music = new();
    private readonly Random _rng = new();

    private readonly List<Rectangle> _invaders = [];
    private readonly List<Rectangle> _shots = [];
    private Rectangle _ship = null!;
    private double _shipX;
    private double _invaderDx = 1.6;
    private int _score;
    private bool _running;
    private int _frame;

    private const double ShipY = 14;       // from bottom
    private const double ShipW = 46, ShipH = 22;
    private const double InvW = 34, InvH = 22;
    private const double ShotW = 4, ShotH = 12, ShotSpeed = 9;

    public SpaceInvadersWindow()
    {
        Title = "Reclaim — Space Reclaimers";
        Width = 760;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x07));

        BuildUi();
        _timer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS
        _timer.Tick += (_, _) => Frame();
        ShowStart();
    }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var hudGrid = new Grid { Margin = new Thickness(16, 12, 16, 8) };
        hudGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hudGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _scoreText.Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0x6B, 0xFF));
        _scoreText.FontWeight = FontWeights.Bold;
        _scoreText.FontSize = 18;
        _scoreText.Text = "Reclaimed: 0";
        _scoreText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_scoreText, 0);

        // Volume control, default 30%.
        var volPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        volPanel.Children.Add(new TextBlock
        {
            Text = "\uE767",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        var volSlider = new Slider
        {
            Minimum = 0, Maximum = 1, Value = 0.30, Width = 110,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Music volume",
        };
        volSlider.ValueChanged += (_, e) => { _music.Volume = e.NewValue; SoundFx.Volume = e.NewValue; };
        volPanel.Children.Add(volSlider);
        Grid.SetColumn(volPanel, 1);

        hudGrid.Children.Add(_scoreText);
        hudGrid.Children.Add(volPanel);
        Grid.SetRow(hudGrid, 0);

        _board.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
        _board.ClipToBounds = true;
        _board.MouseMove += OnMouseMoveBoard;
        _board.MouseLeftButtonDown += OnFire;

        _centerText.Foreground = Brushes.White;
        _centerText.FontSize = 22;
        _centerText.TextAlignment = TextAlignment.Center;
        _centerText.HorizontalAlignment = HorizontalAlignment.Center;
        _centerText.VerticalAlignment = VerticalAlignment.Center;
        _centerText.TextWrapping = TextWrapping.Wrap;

        var overlay = new Grid();
        overlay.Children.Add(_board);
        overlay.Children.Add(_centerText);
        Grid.SetRow(overlay, 1);

        root.Children.Add(hudGrid);
        root.Children.Add(overlay);
        Content = root;
    }

    private void ShowStart()
    {
        _centerText.Text = "Space Reclaimers\n\nMove with the mouse, click to fire.\nClear the junk before it lands!\n\nClick to start";
        _centerText.Visibility = Visibility.Visible;
        _board.MouseLeftButtonDown -= OnFire;
        _board.MouseLeftButtonDown += StartOnClick;
    }

    private void StartOnClick(object sender, MouseButtonEventArgs e)
    {
        _board.MouseLeftButtonDown -= StartOnClick;
        _board.MouseLeftButtonDown += OnFire;
        StartGame();
    }

    private void StartGame()
    {
        _board.Children.Clear();
        _invaders.Clear();
        _shots.Clear();
        _score = 0;
        _invaderDx = 2.8; // faster start
        _running = true;
        _centerText.Visibility = Visibility.Collapsed;

        // Ship.
        _ship = new Rectangle
        {
            Width = ShipW, Height = ShipH,
            RadiusX = 5, RadiusY = 5,
            Fill = new SolidColorBrush(Color.FromRgb(0x2D, 0x6B, 0xFF)),
        };
        _board.Children.Add(_ship);
        _shipX = Math.Max(0, _board.ActualWidth / 2 - ShipW / 2);

        SpawnWave();
        _music.Start();
        _timer.Start();
        UpdateHud();
    }

    private void SpawnWave()
    {
        const int cols = 8, rows = 4;
        const double gapX = 16, gapY = 14, startY = 30;
        var totalW = cols * InvW + (cols - 1) * gapX;
        var startX = Math.Max(10, (_board.ActualWidth - totalW) / 2);

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var inv = new Rectangle
            {
                Width = InvW, Height = InvH, RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x41)), // gold "junk"
            };
            Canvas.SetLeft(inv, startX + c * (InvW + gapX));
            Canvas.SetTop(inv, startY + r * (InvH + gapY));
            _board.Children.Add(inv);
            _invaders.Add(inv);
        }
    }

    private void OnMouseMoveBoard(object sender, MouseEventArgs e)
    {
        if (!_running) return;
        var x = e.GetPosition(_board).X - ShipW / 2;
        _shipX = Math.Clamp(x, 0, Math.Max(0, _board.ActualWidth - ShipW));
    }

    private void OnFire(object sender, MouseButtonEventArgs e)
    {
        if (!_running) return;
        if (_shots.Count >= 6) return; // allow a few more in flight
        var shot = new Rectangle
        {
            Width = ShotW, Height = ShotH,
            Fill = new SolidColorBrush(Color.FromRgb(0x7F, 0xB2, 0xFF)),
        };
        Canvas.SetLeft(shot, _shipX + ShipW / 2 - ShotW / 2);
        Canvas.SetTop(shot, _board.ActualHeight - ShipY - ShipH - ShotH);
        _board.Children.Add(shot);
        _shots.Add(shot);
        SoundFx.Blast();
    }

    private void Frame()
    {
        if (!_running) return;
        _frame++;

        // Ship follows target X.
        Canvas.SetLeft(_ship, _shipX);
        Canvas.SetTop(_ship, _board.ActualHeight - ShipY - ShipH);

        MoveInvaders();
        MoveShots();
        CheckCollisions();

        if (_invaders.Count == 0)
        {
            // Next wave, noticeably faster.
            _invaderDx = Math.Sign(_invaderDx) * (Math.Abs(_invaderDx) + 1.0);
            SpawnWave();
        }
        UpdateHud();
    }

    private void MoveInvaders()
    {
        if (_invaders.Count == 0) return;

        double minX = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var inv in _invaders)
        {
            var l = Canvas.GetLeft(inv);
            minX = Math.Min(minX, l);
            maxX = Math.Max(maxX, l + InvW);
            maxY = Math.Max(maxY, Canvas.GetTop(inv) + InvH);
        }

        var stepDown = false;
        if (maxX + _invaderDx > _board.ActualWidth || minX + _invaderDx < 0)
        {
            _invaderDx = -_invaderDx;
            stepDown = true;
        }

        foreach (var inv in _invaders)
        {
            Canvas.SetLeft(inv, Canvas.GetLeft(inv) + _invaderDx);
            if (stepDown)
                Canvas.SetTop(inv, Canvas.GetTop(inv) + 20); // steeper drop
        }

        // Reached the ship line → game over.
        if (maxY >= _board.ActualHeight - ShipY - ShipH)
            EndGame(false);
    }

    private void MoveShots()
    {
        for (var i = _shots.Count - 1; i >= 0; i--)
        {
            var top = Canvas.GetTop(_shots[i]) - ShotSpeed;
            if (top < -ShotH)
            {
                _board.Children.Remove(_shots[i]);
                _shots.RemoveAt(i);
            }
            else
            {
                Canvas.SetTop(_shots[i], top);
            }
        }
    }

    private void CheckCollisions()
    {
        for (var s = _shots.Count - 1; s >= 0; s--)
        {
            var sx = Canvas.GetLeft(_shots[s]);
            var sy = Canvas.GetTop(_shots[s]);
            for (var i = _invaders.Count - 1; i >= 0; i--)
            {
                var ix = Canvas.GetLeft(_invaders[i]);
                var iy = Canvas.GetTop(_invaders[i]);
                if (sx < ix + InvW && sx + ShotW > ix && sy < iy + InvH && sy + ShotH > iy)
                {
                    _board.Children.Remove(_invaders[i]);
                    _invaders.RemoveAt(i);
                    _board.Children.Remove(_shots[s]);
                    _shots.RemoveAt(s);
                    _score++;
                    break;
                }
            }
        }
    }

    private void EndGame(bool won)
    {
        _running = false;
        _timer.Stop();
        _music.Stop();
        _centerText.Text = $"{(won ? "Cleared!" : "The junk got through!")}\n\n" +
                           $"Reclaimed {_score} junk files\n\nClick to play again";
        _centerText.Visibility = Visibility.Visible;
        _board.MouseLeftButtonDown -= OnFire;
        _board.MouseLeftButtonDown += StartOnClick;
    }

    private void UpdateHud() => _scoreText.Text = $"Reclaimed: {_score}";

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _music.Dispose();
        base.OnClosed(e);
    }
}
