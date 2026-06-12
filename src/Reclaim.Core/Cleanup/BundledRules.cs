namespace Reclaim.Core.Cleanup;

/// <summary>
/// The catalog of reclaimable-location rules bundled with the app. Deliberately
/// conservative: every entry is a well-established, documented reclaimable
/// location, with an honest explanation and a safety tier. Anything that touches
/// system internals is marked <see cref="SafetyTier.UseOfficialTool"/> or
/// <see cref="SafetyTier.Caution"/> and is reported for awareness only.
///
/// This static catalog is v1. A later version will load community-maintained,
/// signed rule files so the knowledge stays current without app updates.
/// </summary>
public static class BundledRules
{
    public static IReadOnlyList<CleanupRule> All { get; } =
    [
        // ---- Shader caches (regenerate automatically) ----------------------
        new CleanupRule
        {
            Id = "nvidia.dxcache",
            Title = "NVIDIA DirectX shader cache",
            Category = CleanupCategory.ShaderCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Compiled GPU shaders cached by NVIDIA's driver. Deleting them is safe; "
                + "games and apps rebuild them on next launch, which may cause brief one-time stutter.",
            PathTemplates =
            [
                @"%LOCALAPPDATA%\NVIDIA\DXCache",
                @"%LOCALAPPDATA%\NVIDIA\GLCache",
                @"%LOCALAPPDATA%\D3DSCache",
            ],
            Reference = "https://learn.microsoft.com/windows/win32/direct3d11/shader-cache",
        },
        new CleanupRule
        {
            Id = "amd.dxcache",
            Title = "AMD shader cache",
            Category = CleanupCategory.ShaderCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Compiled GPU shaders cached by AMD's driver. Safe to delete; rebuilt on demand.",
            PathTemplates = [@"%LOCALAPPDATA%\AMD\DxCache", @"%LOCALAPPDATA%\AMD\GLCache"],
        },
        new CleanupRule
        {
            Id = "steam.shadercache",
            Title = "Steam shader cache",
            Category = CleanupCategory.ShaderCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Pre-compiled shaders Steam downloads/builds per game. Safe to delete; "
                + "Steam rebuilds them, briefly increasing load time and CPU use the next time you play.",
            DirectoryNameMatches = ["shadercache"],
            RequiredAncestorSegments = ["steamapps", "Steam"],
        },

        // ---- Thumbnail cache -----------------------------------------------
        new CleanupRule
        {
            Id = "windows.thumbnails",
            Title = "Windows thumbnail cache",
            Category = CleanupCategory.ThumbnailCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached thumbnail images for File Explorer. Safe to delete; Explorer "
                + "regenerates thumbnails as you browse folders again.",
            PathTemplates = [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"],
        },

        // ---- Temporary files -----------------------------------------------
        new CleanupRule
        {
            Id = "windows.user-temp",
            Title = "User temporary files",
            Category = CleanupCategory.TemporaryFiles,
            Safety = SafetyTier.SafeTransient,
            Explanation = "Per-user temporary files. Most are leftovers apps failed to clean up. "
                + "Generally safe to clear, though files in active use by a running app are skipped by Windows.",
            PathTemplates = [@"%LOCALAPPDATA%\Temp", @"%TEMP%"],
        },

        // ---- Browser caches ------------------------------------------------
        new CleanupRule
        {
            Id = "chrome.cache",
            Title = "Google Chrome cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content for Chrome. Safe to delete; the browser re-downloads "
                + "content as needed. Does not affect bookmarks, passwords, or history.",
            PathTemplates = [@"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Cache"],
        },
        new CleanupRule
        {
            Id = "edge.cache",
            Title = "Microsoft Edge cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content for Edge. Safe to delete; re-downloaded as needed. "
                + "Does not affect bookmarks, passwords, or history.",
            PathTemplates = [@"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Cache"],
        },

        // ---- Developer caches (often very large) ---------------------------
        new CleanupRule
        {
            Id = "npm.cache",
            Title = "npm package cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached npm packages. Safe to delete; npm re-downloads packages when needed. "
                + "Prefer 'npm cache clean --force' but direct deletion is also safe.",
            PathTemplates = [@"%LOCALAPPDATA%\npm-cache", @"%APPDATA%\npm-cache"],
        },
        new CleanupRule
        {
            Id = "nuget.cache",
            Title = "NuGet package cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached NuGet packages for .NET. Safe to delete; restored on next build.",
            PathTemplates = [@"%USERPROFILE%\.nuget\packages"],
        },
        new CleanupRule
        {
            Id = "pip.cache",
            Title = "pip (Python) cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached Python wheels and downloads. Safe to delete; pip re-downloads as needed.",
            PathTemplates = [@"%LOCALAPPDATA%\pip\Cache"],
        },
        new CleanupRule
        {
            Id = "vscode.cache",
            Title = "Visual Studio Code caches",
            Category = CleanupCategory.DeveloperCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached data for VS Code. Safe to delete; rebuilt automatically. "
                + "Does not affect your settings or extensions.",
            PathTemplates =
            [
                @"%APPDATA%\Code\Cache",
                @"%APPDATA%\Code\CachedData",
                @"%APPDATA%\Code\GPUCache",
            ],
        },

        // ---- Crash dumps & logs --------------------------------------------
        new CleanupRule
        {
            Id = "windows.crashdumps",
            Title = "Application crash dumps",
            Category = CleanupCategory.CrashDumpsAndLogs,
            Safety = SafetyTier.SafeTransient,
            Explanation = "Memory dumps written when apps crash. Safe to delete unless you're actively "
                + "diagnosing a crash, in which case these files contain the diagnostic data.",
            PathTemplates = [@"%LOCALAPPDATA%\CrashDumps"],
        },

        // ---- System maintenance (official tool only) -----------------------
        new CleanupRule
        {
            Id = "windows.component-store",
            Title = "Windows component store (WinSxS)",
            Category = CleanupCategory.SystemMaintenance,
            Safety = SafetyTier.UseOfficialTool,
            Explanation = "Superseded Windows update components. This folder's real reclaimable size is "
                + "NOT its on-disk size (much is hard-linked and shared). Never delete it by hand — run "
                + "'DISM /Online /Cleanup-Image /StartComponentCleanup' instead, which removes only what's safe.",
            PathTemplates = [@"%SYSTEMROOT%\WinSxS"],
            Reference = "https://learn.microsoft.com/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder",
        },
        new CleanupRule
        {
            Id = "windows.software-distribution",
            Title = "Windows Update download cache",
            Category = CleanupCategory.SystemMaintenance,
            Safety = SafetyTier.UseOfficialTool,
            Explanation = "Downloaded Windows Update files. Usually cleared automatically. Clearing by hand "
                + "can disrupt pending updates — prefer Disk Cleanup or 'cleanmgr' for this.",
            PathTemplates = [@"%SYSTEMROOT%\SoftwareDistribution\Download"],
        },

        // ---- Caution: depends on user intent -------------------------------
        new CleanupRule
        {
            Id = "windows.old",
            Title = "Previous Windows installation (Windows.old)",
            Category = CleanupCategory.SystemMaintenance,
            Safety = SafetyTier.Caution,
            Explanation = "Your previous Windows installation, kept so you can roll back after an upgrade. "
                + "Often many GB. Safe to remove ONLY if you're certain you won't roll back — and best removed "
                + "via Disk Cleanup, not by hand.",
            PathTemplates = [@"%SYSTEMDRIVE%\Windows.old"],
        },

        // ================================================================
        //  Additional browser caches (regenerate automatically)
        // ================================================================
        new CleanupRule
        {
            Id = "firefox.cache",
            Title = "Mozilla Firefox cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content for Firefox (the per-profile 'cache2' folder). Safe to delete; "
                + "re-downloaded as needed. Does not affect bookmarks, passwords, or history.",
            DirectoryNameMatches = ["cache2"],
            RequiredAncestorSegments = ["Firefox"],
        },
        new CleanupRule
        {
            Id = "brave.cache",
            Title = "Brave browser cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content for Brave. Safe to delete; re-downloaded as needed. "
                + "Does not affect bookmarks, passwords, or history.",
            PathTemplates = [@"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\Default\Cache"],
        },
        new CleanupRule
        {
            Id = "opera.cache",
            Title = "Opera browser cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content for Opera. Safe to delete; re-downloaded as needed.",
            PathTemplates = [@"%LOCALAPPDATA%\Opera Software\Opera Stable\Cache"],
        },
        new CleanupRule
        {
            Id = "vivaldi.cache",
            Title = "Vivaldi browser cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content for Vivaldi. Safe to delete; re-downloaded as needed.",
            PathTemplates = [@"%LOCALAPPDATA%\Vivaldi\User Data\Default\Cache"],
        },

        // ================================================================
        //  Additional developer / package-manager caches (often very large)
        // ================================================================
        new CleanupRule
        {
            Id = "yarn.cache",
            Title = "Yarn package cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached Yarn packages. Safe to delete; re-downloaded when needed.",
            PathTemplates = [@"%LOCALAPPDATA%\Yarn\Cache", @"%APPDATA%\Yarn\Cache"],
        },
        new CleanupRule
        {
            Id = "gradle.cache",
            Title = "Gradle build cache",
            Category = CleanupCategory.DeveloperCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached dependencies and build outputs for Gradle (Java/Android/Kotlin). "
                + "Often several GB. Safe to delete; Gradle re-downloads and rebuilds as needed.",
            PathTemplates = [@"%USERPROFILE%\.gradle\caches"],
        },
        new CleanupRule
        {
            Id = "maven.cache",
            Title = "Maven repository cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Locally cached Maven (Java) artifacts. Safe to delete; re-downloaded on next build.",
            PathTemplates = [@"%USERPROFILE%\.m2\repository"],
        },
        new CleanupRule
        {
            Id = "cargo.cache",
            Title = "Rust Cargo registry cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Downloaded crates and registry data for Rust's Cargo. Safe to delete; "
                + "re-downloaded on next build.",
            PathTemplates = [@"%USERPROFILE%\.cargo\registry"],
        },
        new CleanupRule
        {
            Id = "go.modcache",
            Title = "Go module cache",
            Category = CleanupCategory.PackageManagerCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Downloaded Go modules. Safe to delete (prefer 'go clean -modcache'); "
                + "re-downloaded when needed.",
            PathTemplates = [@"%USERPROFILE%\go\pkg\mod"],
        },
        new CleanupRule
        {
            Id = "gradle.wrapper.dists",
            Title = "Gradle wrapper distributions",
            Category = CleanupCategory.DeveloperCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Downloaded Gradle versions kept by the Gradle wrapper. Safe to delete; "
                + "re-downloaded automatically when a project needs them.",
            PathTemplates = [@"%USERPROFILE%\.gradle\wrapper\dists"],
        },

        // ================================================================
        //  Game launcher caches (regenerate; webcache/shader data)
        // ================================================================
        new CleanupRule
        {
            Id = "epic.webcache",
            Title = "Epic Games Launcher web cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Web cache for the Epic Games Launcher. Safe to delete; the launcher rebuilds it. "
                + "A common fix for a launcher that won't load.",
            PathTemplates = [@"%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache"],
        },
        new CleanupRule
        {
            Id = "battlenet.cache",
            Title = "Battle.net cache",
            Category = CleanupCategory.DeveloperCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cache for Blizzard's Battle.net launcher. Safe to delete; rebuilt automatically.",
            PathTemplates = [@"%LOCALAPPDATA%\Battle.net\Cache"],
        },
        new CleanupRule
        {
            Id = "directx.shadercache",
            Title = "DirectX shader cache",
            Category = CleanupCategory.ShaderCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Windows' DirectX shader cache, shared across games. Safe to delete; "
                + "rebuilt on demand with brief one-time stutter.",
            PathTemplates = [@"%LOCALAPPDATA%\D3DSCache"],
        },

        // ================================================================
        //  More transient system data (safe to clear)
        // ================================================================
        new CleanupRule
        {
            Id = "windows.wer",
            Title = "Windows Error Reporting archives",
            Category = CleanupCategory.CrashDumpsAndLogs,
            Safety = SafetyTier.SafeTransient,
            Explanation = "Archived error reports queued for/after sending to Microsoft. Safe to delete "
                + "unless you're investigating a specific crash report.",
            PathTemplates =
            [
                @"%LOCALAPPDATA%\Microsoft\Windows\WER",
                @"%PROGRAMDATA%\Microsoft\Windows\WER",
            ],
        },
        new CleanupRule
        {
            Id = "windows.inetcache",
            Title = "Internet Explorer / WinINet cache",
            Category = CleanupCategory.BrowserCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached web content used by older Windows components (WinINet/IE). Safe to delete; "
                + "regenerated as needed.",
            PathTemplates = [@"%LOCALAPPDATA%\Microsoft\Windows\INetCache"],
        },
        new CleanupRule
        {
            Id = "windows.temp-system",
            Title = "System temporary files",
            Category = CleanupCategory.TemporaryFiles,
            Safety = SafetyTier.SafeTransient,
            Explanation = "The machine-wide Windows temp folder. Mostly leftovers; files in active use are "
                + "skipped. Safe to clear.",
            PathTemplates = [@"%SYSTEMROOT%\Temp"],
        },
        new CleanupRule
        {
            Id = "windows.font-cache",
            Title = "Windows font cache (service data)",
            Category = CleanupCategory.DeveloperCache,
            Safety = SafetyTier.SafeRegenerates,
            Explanation = "Cached font data rebuilt by the Windows Font Cache service. Safe to delete; "
                + "rebuilt automatically (briefly slower first font rendering).",
            PathTemplates = [@"%SYSTEMROOT%\ServiceProfiles\LocalService\AppData\Local\FontCache"],
        },

        // ================================================================
        //  System maintenance — report only, official tool required
        // ================================================================
        new CleanupRule
        {
            Id = "windows.delivery-optimization",
            Title = "Delivery Optimization files",
            Category = CleanupCategory.SystemMaintenance,
            Safety = SafetyTier.UseOfficialTool,
            Explanation = "Cached update/app files Windows shares with other PCs on your network. Can be "
                + "several GB. Let Windows manage it, or clear via Disk Cleanup ('Delivery Optimization Files') "
                + "rather than by hand.",
            PathTemplates = [@"%SYSTEMROOT%\SoftwareDistribution\DeliveryOptimization"],
        },
    ];
}
