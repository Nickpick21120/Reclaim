using Reclaim.Core.Formatting;
using Reclaim.Core.Scanning;

namespace Reclaim.Core.Cleanup;

public enum DeletionMode
{
    /// <summary>Send to Recycle Bin — recoverable. The default.</summary>
    RecycleBin,
    /// <summary>Permanent deletion — unrecoverable. Explicit user opt-in only.</summary>
    Permanent,
}

public enum DeletionRefusalReason
{
    None,
    /// <summary>The finding's safety tier forbids in-app deletion (UseOfficialTool/Caution).</summary>
    TierForbidsDeletion,
    /// <summary>The path resolves to a protected system-critical location.</summary>
    ProtectedPath,
    /// <summary>The path escaped the finding's matched directory.</summary>
    OutsideMatchedScope,
}

/// <summary>A single file/folder to remove, with its size so the engine can
/// tally the bytes actually freed (vs. skipped because in use).</summary>
public readonly record struct DeletionTarget(string Path, long SizeBytes);

/// <summary>One unit of deletion work: which finding, what mode, and whether it's allowed.</summary>
public sealed class DeletionPlanItem
{
    public required CleanupFinding Finding { get; init; }
    public required bool Allowed { get; init; }
    public required DeletionRefusalReason RefusalReason { get; init; }

    /// <summary>The concrete children that would be removed (contents of the
    /// matched folder; the folder itself is preserved). Empty when not allowed.</summary>
    public required IReadOnlyList<DeletionTarget> Targets { get; init; }

    public long ReclaimableBytes { get; init; }
}

public sealed class DeletionPlan
{
    public required IReadOnlyList<DeletionPlanItem> Items { get; init; }
    public long TotalBytes { get; init; }
    public int AllowedCount { get; init; }
    public int RefusedCount { get; init; }
}

/// <summary>
/// Decides what may be deleted and produces an auditable plan. This class makes
/// NO destructive calls itself — it only plans. Actual file removal is performed
/// by an injected <see cref="IFileRemover"/>, so the dangerous operation is
/// isolated, swappable, and testable with a fake.
///
/// Safety rules enforced here (not in the UI, so they can't be bypassed):
///   1. Only the two safe tiers (SafeRegenerates, SafeTransient) are deletable.
///      UseOfficialTool and Caution are always refused.
///   2. A hard exclusion list blocks system-critical roots even if a rule matched.
///   3. Deletion targets the CONTENTS of a matched directory; the directory
///      itself is preserved.
///   4. Every target path must remain inside the matched finding's directory.
/// </summary>
public sealed class DeletionEngine
{
    private readonly IFileRemover _remover;

    public DeletionEngine(IFileRemover remover) => _remover = remover;

    /// <summary>Tiers that may be deleted from within the app. Everything else
    /// is reported-only and must be handled with official Windows tooling.</summary>
    public static bool IsDeletableTier(SafetyTier tier) =>
        tier is SafetyTier.SafeRegenerates or SafetyTier.SafeTransient;

    /// <summary>Builds an auditable plan from selected findings. Pure and
    /// non-destructive — safe to show to the user before they confirm.</summary>
    public DeletionPlan Plan(IEnumerable<CleanupFinding> selected)
    {
        var items = new List<DeletionPlanItem>();

        foreach (var finding in selected)
        {
            var reason = Evaluate(finding);
            if (reason != DeletionRefusalReason.None)
            {
                items.Add(new DeletionPlanItem
                {
                    Finding = finding,
                    Allowed = false,
                    RefusalReason = reason,
                    Targets = [],
                    ReclaimableBytes = 0,
                });
                continue;
            }

            // Targets = the matched directory's immediate children (contents),
            // keeping the directory itself.
            var targets = finding.Node.Children
                .Where(c => !IsProtectedPath(c.FullPath))
                .Select(c => new DeletionTarget(c.FullPath, c.SizeBytes))
                .ToList();

            items.Add(new DeletionPlanItem
            {
                Finding = finding,
                Allowed = targets.Count > 0,
                RefusalReason = DeletionRefusalReason.None,
                Targets = targets,
                ReclaimableBytes = finding.SizeBytes,
            });
        }

        return new DeletionPlan
        {
            Items = items,
            TotalBytes = items.Where(i => i.Allowed).Sum(i => i.ReclaimableBytes),
            AllowedCount = items.Count(i => i.Allowed),
            RefusedCount = items.Count(i => !i.Allowed),
        };
    }

    /// <summary>Executes an already-built plan. Re-checks each item against the
    /// safety rules immediately before acting (defense in depth: a plan can't be
    /// tampered into deleting something forbidden). Returns a per-item result.</summary>
    public DeletionResult Execute(DeletionPlan plan, DeletionMode mode,
        IProgress<(int done, int total, string current)>? progress = null)
    {
        var outcomes = new List<DeletionItemOutcome>();
        long reclaimed = 0;
        var totalInUse = 0;
        var totalErrors = 0;

        // Total files we'll attempt, for a real progress percentage.
        var totalTargets = plan.Items.Where(i => i.Allowed).Sum(i => i.Targets.Count);
        var doneTargets = 0;

        foreach (var item in plan.Items)
        {
            if (!item.Allowed)
            {
                outcomes.Add(new DeletionItemOutcome(item.Finding, false, 0, 0, 0, "Skipped (not permitted)."));
                continue;
            }

            // Re-validate at execution time — never trust a precomputed flag alone.
            if (Evaluate(item.Finding) != DeletionRefusalReason.None)
            {
                outcomes.Add(new DeletionItemOutcome(item.Finding, false, 0, 0, 0, "Skipped (failed re-check)."));
                continue;
            }

            long itemFreed = 0;
            var removedCount = 0;
            var inUseCount = 0;
            var errorCount = 0;

            foreach (var target in item.Targets)
            {
                // Final per-path guard before the destructive call.
                if (IsProtectedPath(target.Path))
                    continue;

                progress?.Report((doneTargets, totalTargets, target.Path));
                RemovalOutcome result;
                try
                {
                    result = _remover.Remove(target.Path, mode);
                }
                catch
                {
                    result = RemovalOutcome.Error;
                }
                doneTargets++;

                switch (result)
                {
                    case RemovalOutcome.Removed:
                        itemFreed += target.SizeBytes;
                        removedCount++;
                        break;
                    case RemovalOutcome.InUse:
                        inUseCount++;
                        break;
                    default:
                        errorCount++;
                        break;
                }
            }

            reclaimed += itemFreed;
            totalInUse += inUseCount;
            totalErrors += errorCount;

            var succeeded = removedCount > 0 && inUseCount == 0 && errorCount == 0;
            var message = BuildItemMessage(removedCount, inUseCount, errorCount, itemFreed);
            outcomes.Add(new DeletionItemOutcome(item.Finding, succeeded, itemFreed,
                inUseCount, errorCount, message));
        }

        return new DeletionResult
        {
            Outcomes = outcomes,
            BytesReclaimed = reclaimed,
            FilesInUse = totalInUse,
            FilesErrored = totalErrors,
            Mode = mode,
        };
    }

    private static string BuildItemMessage(int removed, int inUse, int errored, long freed)
    {
        if (inUse == 0 && errored == 0)
            return $"Cleaned ({ByteSize.Format(freed)}).";
        var parts = new List<string> { $"freed {ByteSize.Format(freed)}" };
        if (inUse > 0) parts.Add($"{inUse} in use, skipped");
        if (errored > 0) parts.Add($"{errored} errored");
        return string.Join("; ", parts) + ".";
    }

    /// <summary>
    /// Deletes one explicitly-chosen file, for manual management in Storage mode.
    /// The protected-roots check still applies (so e.g. nothing directly at a
    /// drive root). Returns false without acting if protected or empty.
    /// </summary>
    public bool DeleteFile(string filePath, DeletionMode mode)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;
        if (IsProtectedPath(filePath))
            return false;

        return _remover.Remove(filePath, mode) == RemovalOutcome.Removed;
    }

    /// <summary>
    /// Empties a folder — removes each immediate child but keeps the folder
    /// itself. For manual "Delete contents" in Storage mode. Refuses if the
    /// folder is a protected root. Returns the number of children removed; skips
    /// any individual child that is itself protected.
    /// </summary>
    public int DeleteFolderContents(FileSystemNode folder, DeletionMode mode)
    {
        if (folder is null || !folder.IsDirectory)
            return 0;
        if (IsProtectedPath(folder.FullPath))
            return 0;

        var count = 0;
        foreach (var child in folder.Children)
        {
            if (IsProtectedPath(child.FullPath))
                continue;
            if (_remover.Remove(child.FullPath, mode) == RemovalOutcome.Removed)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Deletes one explicitly-chosen folder (the folder itself and all contents),
    /// for manual file management in Storage mode. Unlike the cleanup path this
    /// is NOT rule-bound — it can target any folder — but the protected-roots
    /// blocklist still absolutely applies, so system-critical locations are
    /// refused regardless. Always a single, deliberate, user-initiated action;
    /// never part of a batch. Returns false (without acting) if the path is
    /// protected or doesn't exist.
    /// </summary>
    public bool DeleteFolder(string folderPath, DeletionMode mode)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;
        if (IsProtectedPath(folderPath))
            return false; // hard refusal — cannot delete system-critical roots

        return _remover.Remove(folderPath, mode) == RemovalOutcome.Removed;
    }

    /// <summary>Whether a given folder may be manually deleted (i.e. is not a
    /// protected system root). Used to enable/disable the UI action.</summary>
    public static bool CanDeleteFolder(string folderPath) =>
        !string.IsNullOrWhiteSpace(folderPath) && !IsProtectedPath(folderPath);

    private static DeletionRefusalReason Evaluate(CleanupFinding finding)
    {
        if (!IsDeletableTier(finding.Safety))
            return DeletionRefusalReason.TierForbidsDeletion;
        if (IsProtectedPath(finding.Path))
            return DeletionRefusalReason.ProtectedPath;
        return DeletionRefusalReason.None;
    }

    /// <summary>
    /// Defense-in-depth blocklist: refuses any path that is, or sits at, a
    /// system-critical root, regardless of what rule matched it. This is a
    /// backstop against a malformed or malicious rule — the tier check above is
    /// the primary gate, but this ensures we never delete, say, all of C:\ or
    /// the Windows directory even if a rule somehow pointed there.
    /// </summary>
    public static bool IsProtectedPath(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        // A bare drive root like "C:" or "C:\".
        if (normalized.Length <= 3 && normalized.Contains(':'))
            return true;

        var lower = normalized.ToLowerInvariant();

        // 1) Exact match against expanded env-var roots (works on Windows).
        foreach (var critical in CriticalRoots)
        {
            var expanded = Environment.ExpandEnvironmentVariables(critical)
                .Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
            // Skip tokens that didn't expand (still contain '%') so we don't
            // accidentally match a literal "%systemroot%".
            if (string.IsNullOrEmpty(expanded) || expanded.Contains('%'))
                continue;
            if (lower == expanded)
                return true;
        }

        // 2) Structural match independent of env expansion: <drive>:\<critical>
        // and key subfolders. This holds even if expansion fails, and blocks the
        // critical roots on any drive letter.
        var m = System.Text.RegularExpressions.Regex.Match(
            lower, @"^[a-z]:\\(windows|program files|program files \(x86\)|programdata|users|windows\.old|system volume information|\$recycle\.bin)(\\system32|\\syswow64|\\winsxs)?$");
        if (m.Success)
            return true;

        // The user's profile root itself (but caches under it are fine).
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        if (!string.IsNullOrEmpty(profile) && lower == profile)
            return true;

        return false;
    }

    private static readonly string[] CriticalRoots =
    [
        @"%SYSTEMROOT%",
        @"%SYSTEMROOT%\System32",
        @"%SYSTEMROOT%\WinSxS",
        @"%ProgramFiles%",
        @"%ProgramFiles(x86)%",
        @"%ProgramData%",
        @"%SYSTEMDRIVE%\Users",
        @"%SYSTEMDRIVE%\Windows.old",
    ];
}

/// <summary>The result of attempting to remove a single file/folder.</summary>
public enum RemovalOutcome
{
    /// <summary>Successfully removed.</summary>
    Removed,
    /// <summary>Could not be removed because it's in use / locked by a process.</summary>
    InUse,
    /// <summary>Some other error (permissions, path issue).</summary>
    Error,
}

/// <summary>Performs the actual file removal. Implemented in the app layer with
/// the Windows Shell API; faked in tests so no real files are touched. Returns
/// the outcome so callers can report accurately what was and wasn't freed.</summary>
public interface IFileRemover
{
    RemovalOutcome Remove(string path, DeletionMode mode);
}

public sealed record DeletionItemOutcome(
    CleanupFinding Finding, bool Succeeded, long BytesFreed, int FilesInUse, int FilesErrored, string Message);

public sealed class DeletionResult
{
    public required IReadOnlyList<DeletionItemOutcome> Outcomes { get; init; }
    public long BytesReclaimed { get; init; }
    public int FilesInUse { get; init; }
    public int FilesErrored { get; init; }
    public DeletionMode Mode { get; init; }
}
