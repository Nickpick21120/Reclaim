namespace Reclaim.Core.Cleanup;

/// <summary>How much caution a path warrants before deletion.</summary>
public enum LocationTrust
{
    /// <summary>Ordinary user data — no special warning.</summary>
    Normal,
    /// <summary>A system/program location: deletable in principle, but likely
    /// intentional. The UI should warn clearly before removing.</summary>
    System,
    /// <summary>A protected, system-critical location. Must never be deletable
    /// through convenience features like the duplicate finder.</summary>
    Protected,
}

/// <summary>
/// Classifies where a file lives so UI features (notably the duplicate finder)
/// can hard-block deletion of protected system files and warn on other system
/// locations — without removing the user's ability to act elsewhere.
///
/// This is intentionally separate from <see cref="DeletionEngine.IsProtectedPath"/>:
/// the engine's guard is the hard safety boundary (and is reused here for the
/// Protected level); this adds a softer "System — warn" middle tier on top.
/// </summary>
public static class LocationTrustClassifier
{
    public static LocationTrust Classify(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return LocationTrust.Protected; // treat the unknown as untouchable

        var lower = fullPath.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        // Hard boundary: the engine's root-level guard (drive roots, the bare
        // critical roots, profile root).
        if (DeletionEngine.IsProtectedPath(fullPath))
            return LocationTrust.Protected;

        // PROTECTED (recursive): the genuinely OS-critical tree. Anything inside
        // C:\Windows (and the special system folders) can never be deleted via
        // convenience features — this keeps e.g. C:\Windows\Boot\...\boot.sdi safe.
        // NOTE: Program Files is deliberately NOT here — it holds plenty of
        // deletable game/app data (Steam libraries, mods), so it's "System (warn)"
        // below rather than hard-blocked. Only real OS files are untouchable.
        string[] protectedTrees =
        [
            @"\windows\",
            @"\system volume information\",
            @"\$recycle.bin\",
            @"\windows.old\",
        ];
        foreach (var tree in protectedTrees)
        {
            if (lower.Contains(tree))
                return LocationTrust.Protected;
        }

        // SYSTEM (warn): installed-program and shared-data locations. Deletable —
        // these hold real software but also removable game/app content — so the UI
        // warns clearly and lets the user decide, rather than blocking outright.
        string[] systemMarkers =
        [
            @"\program files\",
            @"\program files (x86)\",
            @"\programdata\",
            @"\appdata\local\microsoft\",
            @"\appdata\roaming\microsoft\",
        ];
        foreach (var marker in systemMarkers)
        {
            if (lower.Contains(marker))
                return LocationTrust.System;
        }

        return LocationTrust.Normal;
    }
}
