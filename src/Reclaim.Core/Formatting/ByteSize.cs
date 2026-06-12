namespace Reclaim.Core.Formatting;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count as a human-readable string, e.g. 1536 → "1.5 KB".</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }
}
