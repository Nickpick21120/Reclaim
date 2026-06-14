using System.Globalization;
using System.Text;
using System.Text.Json;
using Reclaim.Core.Scanning;

namespace Reclaim.Core.Export;

/// <summary>
/// Serializes a scanned tree to CSV (a flat, spreadsheet-friendly row per file
/// and folder) or JSON (the nested tree, structure preserved). Pure string
/// production — the caller decides where to write it. Uses only System.Text.Json,
/// which ships with .NET, so the zero-dependency rule still holds.
/// </summary>
public static class ScanExporter
{
    /// <summary>A flat CSV: one row per node, with full path, type, size, counts,
    /// last-modified, and depth. Easy to open in Excel and sort/pivot.</summary>
    public static string ToCsv(FileSystemNode root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Path,Name,Type,SizeBytes,FileCount,DirectoryCount,LastModifiedUtc,Depth,HadError");
        WriteCsvRows(root, 0, sb);
        return sb.ToString();
    }

    private static void WriteCsvRows(FileSystemNode node, int depth, StringBuilder sb)
    {
        var type = node.IsDirectory ? "Directory" : "File";
        var modified = node.LastWriteUtc == default
            ? ""
            : node.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture);

        sb.Append(Csv(node.FullPath)).Append(',')
          .Append(Csv(node.Name)).Append(',')
          .Append(type).Append(',')
          .Append(node.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(node.FileCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(node.DirectoryCount.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(modified).Append(',')
          .Append(depth.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(node.HadError ? "true" : "false")
          .Append('\n');

        foreach (var child in node.Children)
            WriteCsvRows(child, depth + 1, sb);
    }

    /// <summary>Quote a CSV field per RFC 4180 when it contains a comma, quote,
    /// or newline; double any embedded quotes.</summary>
    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var needsQuoting = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        if (!needsQuoting)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>The nested tree as indented JSON, preserving folder structure.</summary>
    public static string ToJson(FileSystemNode root)
    {
        var dto = ToDto(root);
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static ExportNode ToDto(FileSystemNode node) => new()
    {
        Name = node.Name,
        Path = node.FullPath,
        Type = node.IsDirectory ? "directory" : "file",
        SizeBytes = node.SizeBytes,
        FileCount = node.FileCount,
        DirectoryCount = node.DirectoryCount,
        LastModifiedUtc = node.LastWriteUtc == default
            ? null
            : node.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture),
        HadError = node.HadError ? true : null,
        Children = node.IsDirectory && node.Children.Count > 0
            ? node.Children.Select(ToDto).ToList()
            : null,
    };

    // Serialization shape. Nulls are omitted for compactness where sensible.
    private sealed class ExportNode
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Type { get; set; } = "";
        public long SizeBytes { get; set; }
        public long FileCount { get; set; }
        public long DirectoryCount { get; set; }
        public string? LastModifiedUtc { get; set; }
        public bool? HadError { get; set; }
        public List<ExportNode>? Children { get; set; }
    }
}
