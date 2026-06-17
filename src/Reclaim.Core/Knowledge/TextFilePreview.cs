using System.Text;

namespace Reclaim.Core.Knowledge;

/// <summary>
/// Produces a short, safe preview of a text file's contents for display in a
/// tooltip. Pure logic over a byte buffer (the caller does the actual disk read),
/// so the rules — what counts as text, how much to show, how to bail on binary
/// data — are unit-testable without touching the filesystem.
/// </summary>
public static class TextFilePreview
{
    /// <summary>Read at most this many bytes from a file for a preview.</summary>
    public const int MaxBytes = 8 * 1024;

    /// <summary>Show at most this many lines.</summary>
    public const int MaxLines = 100;

    // Extensions we treat as text. Lower-case, leading dot. An allowlist (rather
    // than "try everything") avoids dumping binary garbage from unknown types.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml",
        ".csv", ".tsv", ".ini", ".cfg", ".conf", ".config", ".toml", ".env",
        ".html", ".htm", ".css", ".js", ".ts", ".jsx", ".tsx", ".cs", ".c", ".h",
        ".cpp", ".hpp", ".cc", ".java", ".py", ".rb", ".go", ".rs", ".php", ".pl",
        ".sh", ".bat", ".cmd", ".ps1", ".psm1", ".sql", ".r", ".lua", ".vb",
        ".gitignore", ".gitattributes", ".editorconfig", ".props", ".targets",
        ".csproj", ".sln", ".gradle", ".kt", ".swift", ".dart", ".scala", ".clj",
        ".tex", ".rst", ".asc", ".srt", ".vtt", ".reg", ".manifest",
    };

    /// <summary>Whether a path's extension is on the text allowlist. Cheap check
    /// the caller can use to decide whether to read the file at all.</summary>
    public static bool LooksLikeTextFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var ext = System.IO.Path.GetExtension(path);
        // Some text files are named only by a leading-dot convention (e.g. ".env",
        // ".gitignore") where GetExtension returns the whole name.
        if (string.IsNullOrEmpty(ext))
        {
            var name = System.IO.Path.GetFileName(path);
            return TextExtensions.Contains(name);
        }
        return TextExtensions.Contains(ext);
    }

    /// <summary>
    /// Build a preview string from raw file bytes (already capped to ~MaxBytes by
    /// the caller). Returns null if the content looks binary (so the caller shows
    /// no preview). Decodes UTF-8 (with BOM handling) and falls back gracefully;
    /// normalizes line endings; truncates to MaxLines with an ellipsis marker.
    /// </summary>
    public static string? BuildPreview(byte[] bytes, int byteCount)
    {
        if (bytes is null || byteCount <= 0)
            return null;

        var len = Math.Min(byteCount, bytes.Length);

        // Binary sniff: a NUL byte in the sampled region almost always means binary.
        // Also bail if there's a high proportion of non-text control bytes.
        var controlCount = 0;
        for (var i = 0; i < len; i++)
        {
            var b = bytes[i];
            if (b == 0)
                return null; // definitely binary
            // Allow tab(9), LF(10), CR(13); count other low control bytes as suspect.
            if (b < 9 || (b > 13 && b < 32))
                controlCount++;
        }
        if (len > 0 && controlCount * 100 / len > 5) // >5% odd control bytes → binary
            return null;

        // Decode. Honor a UTF-8 BOM; otherwise treat as UTF-8 with replacement so a
        // stray invalid byte produces  rather than throwing.
        var start = 0;
        if (len >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            start = 3; // skip UTF-8 BOM
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                       throwOnInvalidBytes: false);
        string text;
        try
        {
            text = decoder.GetString(bytes, start, len - start);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Normalize line endings and cap the number of lines.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var shown = Math.Min(lines.Length, MaxLines);
        var sb = new StringBuilder();
        for (var i = 0; i < shown; i++)
        {
            // Trim very long single lines so the tooltip doesn't explode horizontally.
            var line = lines[i];
            if (line.Length > 200)
                line = line[..200] + " …";
            sb.Append(line);
            if (i < shown - 1)
                sb.Append('\n');
        }
        if (lines.Length > MaxLines)
            sb.Append("\n… (preview truncated)");

        return sb.ToString();
    }
}
