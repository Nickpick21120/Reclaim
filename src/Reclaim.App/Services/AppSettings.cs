using System.IO;
using System.Text.Json;

namespace Reclaim.App.Services;

/// <summary>
/// User preferences persisted across sessions to %APPDATA%\Reclaim\settings.json.
/// Deliberately tiny and forgiving: any read/write failure falls back to defaults
/// rather than disrupting the app.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Which pane the app opens in: "Treemap" or "List".</summary>
    public string DefaultView { get; set; } = "Treemap";

    /// <summary>If true, deletions default to permanent (skip Recycle Bin). Defaults
    /// to false — Recycle Bin — because permanent deletion is unrecoverable and
    /// should be a deliberate choice, not a casual default.</summary>
    public bool DefaultPermanentDelete { get; set; }

    /// <summary>If true, the last scanned folder is remembered and pre-filled on
    /// next launch.</summary>
    public bool RememberLastFolder { get; set; } = true;

    /// <summary>The last folder scanned (only used when RememberLastFolder is on).</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>EXPERIMENTAL: use the raw-MFT fast scanner for whole-NTFS-drive scans
    /// when running elevated. Falls back to the normal scanner otherwise. Off by
    /// default — this is unverified on the user's hardware until they enable it.</summary>
    public bool ExperimentalMftScan { get; set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Reclaim");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults silently.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Non-fatal: a failed save just means preferences don't persist this time.
        }
    }
}
