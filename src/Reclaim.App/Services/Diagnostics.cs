using System.IO;
using System.Reflection;
using System.Text;

namespace Reclaim.App.Services;

/// <summary>
/// Crash diagnostics: writes detailed crash reports to the app's local-data
/// folder, and tracks a "running" marker so the next launch can detect that the
/// previous run ended unexpectedly. Nothing is ever sent automatically — the UI
/// shows the user the full report and lets them choose to send it.
/// </summary>
public static class Diagnostics
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reclaim");

    private static string CrashReportPath => Path.Combine(Dir, "last-crash.txt");

    /// <summary>The GitHub repo "new issue" base URL. Update if the repo moves.</summary>
    public const string IssuesUrl = "https://github.com/Nickpick21120/Reclaim/issues/new";

    private static void EnsureDir()
    {
        try { Directory.CreateDirectory(Dir); } catch { /* best effort */ }
    }

    /// <summary>App version string, e.g. "0.16.2".</summary>
    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>Gather environment details that help diagnose a crash. Deliberately
    /// limited to non-identifying system info plus whatever is in the exception.</summary>
    public static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Reclaim version: {AppVersion}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"64-bit OS/process: {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess}");
        sb.AppendLine($"Processors: {Environment.ProcessorCount}");
        sb.AppendLine($"Time: {DateTime.Now:u}");
        return sb.ToString();
    }

    /// <summary>Write a crash report (overwrites any previous one).</summary>
    public static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            EnsureDir();
            var report = BuildReport(source, ex);
            File.WriteAllText(CrashReportPath, report);
        }
        catch
        {
            // Logging must never throw.
        }
    }

    /// <summary>The full, human-readable report shown to the user before sending.</summary>
    public static string BuildReport(string source, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Reclaim crash report ===");
        sb.AppendLine($"Source: {source}");
        sb.AppendLine();
        sb.AppendLine(SystemInfo());
        sb.AppendLine("--- Exception ---");
        sb.AppendLine(ex?.ToString() ?? "(no exception details captured)");
        return sb.ToString();
    }

    /// <summary>Read the stored crash report, or null if none.</summary>
    public static string? ReadLastCrash()
    {
        try
        {
            return File.Exists(CrashReportPath) ? File.ReadAllText(CrashReportPath) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void ClearLastCrash()
    {
        try { if (File.Exists(CrashReportPath)) File.Delete(CrashReportPath); }
        catch { /* best effort */ }
    }

    /// <summary>Build a GitHub "new issue" URL with the report pre-filled in the body.
    /// The user reviews and submits it in their browser — nothing is sent silently.</summary>
    public static string BuildIssueUrl(string report)
    {
        var title = Uri.EscapeDataString($"Crash report (v{AppVersion})");
        // Wrap the report in a code block; cap length so the URL stays usable.
        var trimmed = report.Length > 4000 ? report[..4000] + "\n...(truncated)" : report;
        var body = Uri.EscapeDataString(
            "**What happened (please describe):**\n\n\n" +
            "**Diagnostics (auto-generated — review before sending):**\n\n```\n" + trimmed + "\n```\n");
        return $"{IssuesUrl}?title={title}&body={body}";
    }
}
