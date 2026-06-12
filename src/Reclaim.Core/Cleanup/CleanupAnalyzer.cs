using Reclaim.Core.Scanning;

namespace Reclaim.Core.Cleanup;

/// <summary>
/// Cross-references a completed scan against a catalog of <see cref="CleanupRule"/>
/// to find reclaimable locations. This is deliberately conservative: a directory
/// is reported only when it matches a rule by an exact resolved path or by an
/// explicit directory-name rule (optionally constrained by required ancestors).
/// Nothing here deletes; it only describes.
/// </summary>
public sealed class CleanupAnalyzer
{
    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly Func<string, string> _expandEnv;

    /// <param name="rules">The rule catalog to match against.</param>
    /// <param name="expandEnvironment">Resolves %VAR% tokens in path templates.
    /// Injectable so tests are deterministic and platform-independent.</param>
    public CleanupAnalyzer(
        IReadOnlyList<CleanupRule> rules,
        Func<string, string>? expandEnvironment = null)
    {
        _rules = rules;
        _expandEnv = expandEnvironment ?? Environment.ExpandEnvironmentVariables;
    }

    public CleanupReport Analyze(FileSystemNode root)
    {
        // Index the tree by normalized full path for O(1) exact-path lookups.
        var byPath = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
        IndexTree(root, byPath);

        var findings = new List<CleanupFinding>();
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in _rules)
        {
            // 1) Exact resolved-path matches.
            foreach (var template in rule.PathTemplates)
            {
                var resolved = NormalizePath(_expandEnv(template));
                if (byPath.TryGetValue(resolved, out var node) &&
                    node.IsDirectory &&
                    node.SizeBytes > 0 &&
                    claimedPaths.Add(node.FullPath))
                {
                    findings.Add(new CleanupFinding { Rule = rule, Node = node });
                }
            }

            // 2) Directory-name matches anywhere under the root.
            if (rule.DirectoryNameMatches.Count > 0)
                MatchByName(root, rule, claimedPaths, findings);
        }

        return Summarize(findings);
    }

    private void MatchByName(
        FileSystemNode node, CleanupRule rule,
        HashSet<string> claimed, List<CleanupFinding> findings)
    {
        if (node.IsDirectory && node.SizeBytes > 0 &&
            NameMatches(rule, node.Name) &&
            AncestorsSatisfy(rule, node) &&
            claimed.Add(node.FullPath))
        {
            findings.Add(new CleanupFinding { Rule = rule, Node = node });
            // Don't descend into a matched cache — its children are part of it.
            return;
        }

        foreach (var child in node.Children)
        {
            if (child.IsDirectory)
                MatchByName(child, rule, claimed, findings);
        }
    }

    private static bool NameMatches(CleanupRule rule, string name)
    {
        foreach (var candidate in rule.DirectoryNameMatches)
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool AncestorsSatisfy(CleanupRule rule, FileSystemNode node)
    {
        if (rule.RequiredAncestorSegments.Count == 0)
            return true;

        // Walk up the ancestry; require that at least one required segment
        // appears as some ancestor's name.
        for (var p = node.Parent; p is not null; p = p.Parent)
            foreach (var seg in rule.RequiredAncestorSegments)
                if (string.Equals(seg, p.Name, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    private static void IndexTree(FileSystemNode node, Dictionary<string, FileSystemNode> sink)
    {
        if (node.IsDirectory)
        {
            sink[NormalizePath(node.FullPath)] = node;
            foreach (var child in node.Children)
                IndexTree(child, sink);
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    private static CleanupReport Summarize(List<CleanupFinding> findings)
    {
        var categories = findings
            .GroupBy(f => f.Category)
            .Select(g => new CleanupCategorySummary
            {
                Category = g.Key,
                Findings = g.OrderByDescending(f => f.SizeBytes).ToList(),
                TotalBytes = g.Sum(f => f.SizeBytes),
                HighestRisk = g.Max(f => f.Safety),
            })
            .OrderByDescending(c => c.TotalBytes)
            .ToList();

        long total = 0, safe = 0;
        foreach (var f in findings)
        {
            total += f.SizeBytes;
            if (f.Safety is SafetyTier.SafeRegenerates or SafetyTier.SafeTransient)
                safe += f.SizeBytes;
        }

        return new CleanupReport
        {
            Categories = categories,
            AllFindings = findings.OrderByDescending(f => f.SizeBytes).ToList(),
            TotalReclaimableBytes = total,
            SafelyReclaimableBytes = safe,
        };
    }
}
