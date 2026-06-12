namespace Reclaim.Core.Cleanup;

/// <summary>
/// How confident we are that removing a matched location is safe, and what the
/// user should understand before doing so. Ordered from safest to most dangerous.
/// The app NEVER deletes anything in v1 — this tier drives explanation, color,
/// and sort order in the reporting UI, and will gate any future deletion.
/// </summary>
public enum SafetyTier
{
    /// <summary>Regenerated automatically by the owning app. Removal is harmless
    /// beyond a one-time rebuild cost (e.g. shader caches, thumbnail caches).</summary>
    SafeRegenerates,

    /// <summary>Transient data the system/app no longer needs (e.g. temp folders,
    /// old logs, crash dumps). Removal is normally safe but may erase diagnostic
    /// history.</summary>
    SafeTransient,

    /// <summary>Reclaimable, but only through an official tool rather than file
    /// deletion (e.g. Windows component store via DISM, driver packages via
    /// pnputil). Listed for awareness; never delete these by hand.</summary>
    UseOfficialTool,

    /// <summary>Could be reclaimable but depends on user intent or carries real
    /// risk (e.g. old downloads, Windows.old, hibernation file). Always requires
    /// a human decision.</summary>
    Caution,
}

public enum CleanupCategory
{
    ShaderCache,
    ThumbnailCache,
    TemporaryFiles,
    BrowserCache,
    DeveloperCache,
    CrashDumpsAndLogs,
    PackageManagerCache,
    SystemMaintenance,
    RecycleBin,
    Other,
}

/// <summary>
/// A single rule describing one kind of reclaimable location. Rules are matched
/// against scanned paths; a match becomes a <see cref="CleanupFinding"/>.
///
/// Paths use environment-variable tokens (e.g. %LOCALAPPDATA%) resolved at match
/// time. A rule may also require a directory-name pattern deeper in the tree
/// (e.g. any folder literally named "DXCache") rather than a fixed absolute path.
/// </summary>
public sealed class CleanupRule
{
    /// <summary>Stable identifier, e.g. "nvidia.dx-shader-cache". Used in tests,
    /// future user overrides, and telemetry-free logging.</summary>
    public required string Id { get; init; }

    /// <summary>Human-facing name, e.g. "NVIDIA DirectX shader cache".</summary>
    public required string Title { get; init; }

    public required CleanupCategory Category { get; init; }
    public required SafetyTier Safety { get; init; }

    /// <summary>Plain-language description of what the data is and the consequence
    /// of removing it. Shown verbatim to the user. This is the most important
    /// field for an informed decision.</summary>
    public required string Explanation { get; init; }

    /// <summary>Absolute path templates with %ENV% tokens. A finding is produced
    /// for each that resolves to an existing scanned directory.</summary>
    public IReadOnlyList<string> PathTemplates { get; init; } = [];

    /// <summary>Optional: match ANY directory whose name equals one of these
    /// (case-insensitive), anywhere under the scan root. Use for caches that
    /// appear per-app under unpredictable parents, e.g. "shadercache".</summary>
    public IReadOnlyList<string> DirectoryNameMatches { get; init; } = [];

    /// <summary>If set, a directory-name match only counts when one of these
    /// path segments also appears in the ancestry (case-insensitive), to avoid
    /// over-matching a common name like "cache" everywhere.</summary>
    public IReadOnlyList<string> RequiredAncestorSegments { get; init; } = [];

    /// <summary>Optional source/citation for why this is considered reclaimable,
    /// e.g. a Microsoft Learn URL. Surfaced in the UI for transparency.</summary>
    public string? Reference { get; init; }
}
