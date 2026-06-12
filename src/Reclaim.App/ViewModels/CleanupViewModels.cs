using System.Windows.Media;
using Reclaim.Core.Cleanup;
using Reclaim.Core.Formatting;

namespace Reclaim.App.ViewModels;

/// <summary>Display helpers mapping safety tiers to text and color.</summary>
public static class SafetyDisplay
{
    public static string Label(SafetyTier tier) => tier switch
    {
        SafetyTier.SafeRegenerates => "Safe — regenerates",
        SafetyTier.SafeTransient => "Safe — temporary",
        SafetyTier.UseOfficialTool => "Use Windows tool",
        SafetyTier.Caution => "Caution",
        _ => "Unknown",
    };

    /// <summary>Hex color per tier. Greens for safe, blue for tool-managed,
    /// gold (the theme's attention color) for caution.</summary>
    public static string Color(SafetyTier tier) => tier switch
    {
        SafetyTier.SafeRegenerates => "#FF4FB477",
        SafetyTier.SafeTransient => "#FF67A98C",
        SafetyTier.UseOfficialTool => "#FF4F8FE8",
        SafetyTier.Caution => "#FFD9A441",
        _ => "#FF7A8398",
    };
}

public sealed class CleanupFindingViewModel(CleanupFinding finding) : ViewModelBase
{
    public CleanupFinding Finding { get; } = finding;

    public string Title => Finding.Rule.Title;
    public string Path => Finding.Path;
    public string SizeText => ByteSize.Format(Finding.SizeBytes);
    public long SizeBytes => Finding.SizeBytes;
    public string Explanation => Finding.Rule.Explanation;
    public string? Reference => Finding.Rule.Reference;
    public string SafetyLabel => SafetyDisplay.Label(Finding.Safety);
    public Brush SafetyBrush => Brushed(SafetyDisplay.Color(Finding.Safety));

    /// <summary>Whether this finding can be cleaned in-app (safe tiers only).</summary>
    public bool IsDeletable => DeletionEngine.IsDeletableTier(Finding.Safety);

    /// <summary>For non-deletable findings, the guidance shown instead of a checkbox.</summary>
    public string ActionHint => Finding.Safety switch
    {
        SafetyTier.UseOfficialTool => "Use the Windows tool described above — not removable here.",
        SafetyTier.Caution => "Requires your judgment — not removable here.",
        _ => "",
    };

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected && IsDeletable;
        set { if (IsDeletable) Set(ref _isSelected, value); }
    }

    private static Brush Brushed(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

public sealed class CleanupCategoryViewModel : ViewModelBase
{
    public CleanupCategoryViewModel(CleanupCategorySummary summary)
    {
        Summary = summary;
        Findings = summary.Findings.Select(f => new CleanupFindingViewModel(f)).ToList();
    }

    public CleanupCategorySummary Summary { get; }
    public IReadOnlyList<CleanupFindingViewModel> Findings { get; }

    /// <summary>True if any finding in this category can be cleaned in-app.</summary>
    public bool HasDeletable => Findings.Any(f => f.IsDeletable);

    /// <summary>Category checkbox: toggles all deletable findings at once.</summary>
    private bool _selectAll;
    public bool SelectAll
    {
        get => _selectAll;
        set
        {
            if (Set(ref _selectAll, value))
                foreach (var f in Findings)
                    f.IsSelected = value;
        }
    }

    public string Name => Display(Summary.Category);
    public string SizeText => ByteSize.Format(Summary.TotalBytes);
    public string CountText => $"{Summary.LocationCount} location{(Summary.LocationCount == 1 ? "" : "s")}";
    public string SafetyLabel => SafetyDisplay.Label(Summary.HighestRisk);
    public Brush SafetyBrush
    {
        get
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(
                SafetyDisplay.Color(Summary.HighestRisk))!;
            b.Freeze();
            return b;
        }
    }

    private static string Display(CleanupCategory c) => c switch
    {
        CleanupCategory.ShaderCache => "Shader caches",
        CleanupCategory.ThumbnailCache => "Thumbnail cache",
        CleanupCategory.TemporaryFiles => "Temporary files",
        CleanupCategory.BrowserCache => "Browser caches",
        CleanupCategory.DeveloperCache => "Developer caches",
        CleanupCategory.CrashDumpsAndLogs => "Crash dumps & logs",
        CleanupCategory.PackageManagerCache => "Package manager caches",
        CleanupCategory.SystemMaintenance => "System maintenance",
        CleanupCategory.RecycleBin => "Recycle Bin",
        _ => "Other",
    };
}
