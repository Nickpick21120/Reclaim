using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Reclaim.Core.Knowledge;

namespace Reclaim.App;

/// <summary>Maps a <see cref="RemovalSafety"/> to a short badge label.</summary>
public sealed class SafetyToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RemovalSafety s ? Label(s) : "";

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();

    public static string Label(RemovalSafety s) => s switch
    {
        RemovalSafety.SafeRegenerates => "Safe — regenerates",
        RemovalSafety.SafeTransient => "Safe — temporary",
        RemovalSafety.PersonalData => "Your data",
        RemovalSafety.SystemManaged => "System — use tools",
        _ => "Review first",
    };
}

/// <summary>Maps a <see cref="RemovalSafety"/> to a badge background brush.</summary>
public sealed class SafetyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value is RemovalSafety s ? Color(s) : "#FF9A9AA8";
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();

    public static string Color(RemovalSafety s) => s switch
    {
        RemovalSafety.SafeRegenerates => "#FF5BD18B", // green
        RemovalSafety.SafeTransient => "#FF6FC8A8",   // teal-green
        RemovalSafety.PersonalData => "#FF8FB7FF",     // soft blue
        RemovalSafety.SystemManaged => "#FFD9A441",    // gold/caution
        _ => "#FFB9B9C6",                              // grey
    };
}
