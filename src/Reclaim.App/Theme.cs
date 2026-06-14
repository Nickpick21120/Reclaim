using System.Windows.Media;

namespace Reclaim.App;

/// <summary>
/// The single source of truth for colors used in code-behind windows (dialogs,
/// games), mirroring the XAML theme in App.xaml. Centralizing these removes the
/// colour drift that crept in when each window hardcoded its own hex values, and
/// gives one place to swap when a light/dark theme is added later.
///
/// XAML elements should keep using the {StaticResource ...Brush} keys; this class
/// is for the windows that build their UI in C# and can't reference XAML resources
/// as conveniently.
/// </summary>
public static class Theme
{
    // Core palette — kept identical to the Color keys in App.xaml.
    public static readonly Color Bg = Hex(0x05, 0x05, 0x07);          // window background
    public static readonly Color Panel = Hex(0x0B, 0x0D, 0x12);       // sunken panel
    public static readonly Color PanelRaised = Hex(0x12, 0x15, 0x1D); // card / raised panel
    public static readonly Color Border = Hex(0x1E, 0x24, 0x33);
    public static readonly Color Text = Hex(0xDB, 0xE2, 0xF0);        // primary text
    public static readonly Color TextDim = Hex(0x7A, 0x83, 0x98);     // secondary text
    public static readonly Color Accent = Hex(0x2D, 0x6B, 0xFF);      // ultrablue

    // Semantic colors used by trust badges and warnings.
    public static readonly Color Warn = Hex(0xD9, 0xA4, 0x41);        // gold — "system, warn"
    public static readonly Color Danger = Hex(0xC4, 0x2B, 0x1C);      // red — "protected"

    // Frozen brushes (frozen = cheaper, shareable across threads/elements).
    public static readonly SolidColorBrush BgBrush = Frozen(Bg);
    public static readonly SolidColorBrush PanelBrush = Frozen(Panel);
    public static readonly SolidColorBrush PanelRaisedBrush = Frozen(PanelRaised);
    public static readonly SolidColorBrush BorderBrush = Frozen(Border);
    public static readonly SolidColorBrush TextBrush = Frozen(Text);
    public static readonly SolidColorBrush TextDimBrush = Frozen(TextDim);
    public static readonly SolidColorBrush AccentBrush = Frozen(Accent);
    public static readonly SolidColorBrush WarnBrush = Frozen(Warn);
    public static readonly SolidColorBrush DangerBrush = Frozen(Danger);

    private static Color Hex(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
