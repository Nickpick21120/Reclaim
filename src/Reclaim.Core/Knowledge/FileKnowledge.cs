namespace Reclaim.Core.Knowledge;

/// <summary>How confident we are that removing this is safe.</summary>
public enum RemovalSafety
{
    /// <summary>Regenerated automatically; safe to remove.</summary>
    SafeRegenerates,
    /// <summary>Transient/temporary; generally safe to remove.</summary>
    SafeTransient,
    /// <summary>Personal data — removing means real data loss. Keep unless you're sure.</summary>
    PersonalData,
    /// <summary>System-managed; use official tools, don't delete by hand.</summary>
    SystemManaged,
    /// <summary>Unknown / depends; review before touching.</summary>
    Unknown,
}

/// <summary>How an entry is matched against a filesystem node.</summary>
public enum KnowledgeMatch
{
    ExactName,   // e.g. "hiberfil.sys"
    FolderName,  // e.g. "node_modules"
    Extension,   // e.g. ".tmp"
}

/// <summary>One entry in the file-knowledge catalog: a recognizable file, folder,
/// or extension with a plain-language description and removal guidance.</summary>
public sealed class FileKnowledge
{
    public required KnowledgeMatch MatchKind { get; init; }

    /// <summary>The token to match: a filename, folder name, or extension
    /// (including the leading dot for extensions). Compared case-insensitively.</summary>
    public required string Token { get; init; }

    /// <summary>Short human title, e.g. "Hibernation file".</summary>
    public required string Title { get; init; }

    /// <summary>What this is, in plain language.</summary>
    public required string Description { get; init; }

    /// <summary>What program or process creates it.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Typical size range, free-form (e.g. "Often several GB").</summary>
    public required string TypicalSize { get; init; }

    public required RemovalSafety Safety { get; init; }

    /// <summary>One-line verdict on removing it.</summary>
    public required string SafetyNote { get; init; }
}

/// <summary>The resolved description for a specific node, plus the matched entry.</summary>
public sealed class FileInfoResult
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string CreatedBy { get; init; }
    public required string TypicalSize { get; init; }
    public required RemovalSafety Safety { get; init; }
    public required string SafetyNote { get; init; }

    /// <summary>True when this came from the catalog; false for a generic fallback.</summary>
    public required bool IsKnown { get; init; }
}
