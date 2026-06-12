using Reclaim.Core.Cleanup;
using Reclaim.Core.Duplicates;
using Reclaim.Core.Formatting;
using Reclaim.Core.Knowledge;
using Reclaim.Core.Scanning;

var failures = 0;

void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    if (!condition) failures++;
}

static void WriteFile(string path, int bytes)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[bytes]);
}

// ---- Build a synthetic tree with known sizes ----------------------------
var root = Path.Join(Path.GetTempPath(), "reclaim-test-" + Guid.NewGuid().ToString("N"));
WriteFile(Path.Join(root, "big.bin"), 5000);
WriteFile(Path.Join(root, "small.txt"), 100);
WriteFile(Path.Join(root, "sub1", "a.dat"), 2000);
WriteFile(Path.Join(root, "sub1", "nested", "b.dat"), 3000);
WriteFile(Path.Join(root, "sub2", "c.dat"), 1500);
Directory.CreateDirectory(Path.Join(root, "empty"));

var scanner = new DirectoryScanner();
var options = new ScanOptions { ParallelDepth = 2 };

// ---- Test 1: totals and structure ---------------------------------------
var result = await scanner.ScanAsync(root, options);
var r = result.Root;

Check(r.SizeBytes == 11_600, $"root size aggregates correctly (got {r.SizeBytes}, want 11600)");
Check(r.FileCount == 5, $"root file count (got {r.FileCount}, want 5)");
Check(r.DirectoryCount == 4, $"root dir count (got {r.DirectoryCount}, want 4)");
Check(result.FilesScanned == 5, $"counter: files scanned (got {result.FilesScanned})");
Check(result.ErrorCount == 0, "no errors on clean tree");

var sub1 = r.Children.FirstOrDefault(c => c.Name == "sub1");
Check(sub1 is { SizeBytes: 5000, FileCount: 2, DirectoryCount: 1 }, "sub1 aggregates nested dir");

var sortedDesc = r.Children.Zip(r.Children.Skip(1)).All(p => p.First.SizeBytes >= p.Second.SizeBytes);
Check(sortedDesc, "children sorted by size descending");

var empty = r.Children.FirstOrDefault(c => c.Name == "empty");
Check(empty is { SizeBytes: 0, FileCount: 0 }, "empty directory handled");

Check(Math.Abs(sub1!.FractionOfParent - 5000.0 / 11600.0) < 1e-9, "FractionOfParent computed");

// ---- Test 2: reparse points / symlinks are skipped -----------------------
if (!OperatingSystem.IsWindows())
{
    File.CreateSymbolicLink(Path.Join(root, "link-to-sub1"), Path.Join(root, "sub1"));
    var withLink = await scanner.ScanAsync(root, options);
    Check(withLink.Root.SizeBytes == 11_600, "symlinked directory not double-counted");
    Check(withLink.Root.Children.All(c => c.Name != "link-to-sub1"), "symlink excluded from tree");
}

// ---- Test 3: inaccessible directory is recorded, scan completes ----------
var locked = Path.Join(root, "locked");
WriteFile(Path.Join(locked, "secret.bin"), 999);
var canLock = !OperatingSystem.IsWindows() && Environment.UserName != "root";
if (canLock)
{
    File.SetUnixFileMode(locked, UnixFileMode.None);
    var withLocked = await scanner.ScanAsync(root, options);
    File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    Check(withLocked.ErrorCount >= 1, "inaccessible dir counted as error");
    Check(withLocked.Root.HadError, "error flag propagates to root");
    var lockedNode = withLocked.Root.Children.First(c => c.Name == "locked");
    Check(lockedNode.HadError && lockedNode.SizeBytes == 0, "locked dir marked, size 0");
}
else
{
    Console.WriteLine("SKIP  inaccessible-dir test (running as root or on Windows)");
}

// ---- Test 4: cancellation -------------------------------------------------
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    try
    {
        await scanner.ScanAsync(root, options, cancellationToken: cts.Token);
        Check(false, "cancellation throws OperationCanceledException");
    }
    catch (OperationCanceledException)
    {
        Check(true, "cancellation throws OperationCanceledException");
    }
}

// ---- Test 5: progress is reported ----------------------------------------
var reports = 0;
var progress = new Progress<ScanProgress>(_ => Interlocked.Increment(ref reports));
await scanner.ScanAsync(root, options, progress);
await Task.Delay(50); // Progress<T> posts asynchronously
Check(reports >= 1, $"progress callback fired (got {reports})");

// ---- Test 6: byte formatting ----------------------------------------------
Check(ByteSize.Format(0) == "0 B", "format 0 B");
Check(ByteSize.Format(1023) == "1023 B", "format 1023 B");
Check(ByteSize.Format(1536) == "1.5 KB", "format 1.5 KB");
Check(ByteSize.Format(5L * 1024 * 1024 * 1024) == "5 GB", "format 5 GB");

// ---- Test 7: missing root throws -----------------------------------------
try
{
    await scanner.ScanAsync(Path.Join(root, "does-not-exist"), options);
    Check(false, "missing root throws DirectoryNotFoundException");
}
catch (DirectoryNotFoundException)
{
    Check(true, "missing root throws DirectoryNotFoundException");
}

Directory.Delete(root, recursive: true);

// ---- Test 8: squarified treemap layout -------------------------------------
{
    var sizes = new long[] { 6000, 3000, 2000, 1000, 500, 300, 200 };
    var bounds = new Reclaim.Core.Treemap.RectD(0, 0, 800, 500);
    var rects = Reclaim.Core.Treemap.Squarifier.Layout(sizes, bounds);

    Check(rects.Length == sizes.Length, "squarify: one rect per item");

    // Areas proportional to sizes.
    double total = sizes.Sum();
    var proportional = true;
    for (var i = 0; i < sizes.Length; i++)
    {
        var expected = sizes[i] / total * bounds.Area;
        if (Math.Abs(rects[i].Area - expected) > 0.01)
            proportional = false;
    }
    Check(proportional, "squarify: areas proportional to sizes");

    // Rects exactly tile the bounds (areas sum, all inside).
    Check(Math.Abs(rects.Sum(r => r.Area) - bounds.Area) < 0.01, "squarify: rects tile full area");
    Check(rects.All(r =>
            r.X >= -0.01 && r.Y >= -0.01 &&
            r.Right <= bounds.Width + 0.01 && r.Bottom <= bounds.Height + 0.01),
        "squarify: all rects inside bounds");

    // No pair overlaps (sample midpoints against all other rects).
    var overlap = false;
    for (var i = 0; i < rects.Length && !overlap; i++)
    for (var j = 0; j < rects.Length; j++)
    {
        if (i == j) continue;
        var cx = rects[i].X + rects[i].Width / 2;
        var cy = rects[i].Y + rects[i].Height / 2;
        if (rects[j].Contains(cx, cy)) { overlap = true; break; }
    }
    Check(!overlap, "squarify: no overlapping rects");

    // Aspect ratios are sane (the whole point of squarification).
    var worstAspect = rects.Max(r => Math.Max(r.Width / r.Height, r.Height / r.Width));
    Check(worstAspect < 4.0, $"squarify: aspect ratios reasonable (worst {worstAspect:0.00})");

    // Degenerate inputs don't crash.
    Check(Reclaim.Core.Treemap.Squarifier.Layout([], bounds).Length == 0, "squarify: empty input");
    Check(Reclaim.Core.Treemap.Squarifier.Layout([42], bounds)[0].Area > 0, "squarify: single item fills bounds");
    var thin = Reclaim.Core.Treemap.Squarifier.Layout(sizes, new Reclaim.Core.Treemap.RectD(0, 0, 1000, 2));
    Check(thin.Length == sizes.Length, "squarify: survives extreme aspect bounds");
}

// ---- Test 9: flat list (largest files / folders) ---------------------------
{
    var fl = Path.Join(Path.GetTempPath(), "reclaim-flat-" + Guid.NewGuid().ToString("N"));
    WriteFile(Path.Join(fl, "huge.bin"), 9000);
    WriteFile(Path.Join(fl, "docs", "mid.txt"), 4000);
    WriteFile(Path.Join(fl, "docs", "deep", "tiny.txt"), 50);
    WriteFile(Path.Join(fl, "media", "clip.mp4"), 7000);

    var flScanner = new DirectoryScanner();
    var flRoot = (await flScanner.ScanAsync(fl, new ScanOptions())).Root;

    var files = Reclaim.Core.Scanning.FlatList.Largest(
        flRoot, Reclaim.Core.Scanning.FlatListKind.Files);
    Check(files.Count == 4, $"flatlist: all files collected (got {files.Count})");
    Check(files.All(f => !f.IsDirectory), "flatlist: files mode returns only files");
    Check(files[0].Name == "huge.bin" && files[1].Name == "clip.mp4",
        "flatlist: files sorted by size descending");
    var filesSorted = files.Zip(files.Skip(1)).All(p => p.First.SizeBytes >= p.Second.SizeBytes);
    Check(filesSorted, "flatlist: full descending order");

    var folders = Reclaim.Core.Scanning.FlatList.Largest(
        flRoot, Reclaim.Core.Scanning.FlatListKind.Folders);
    Check(folders.All(d => d.IsDirectory), "flatlist: folders mode returns only directories");
    Check(folders.All(d => d.FullPath != flRoot.FullPath),
        "flatlist: root excluded from folders");
    // docs (4050) ranks above media (7000)? no — media bigger; deep (50) smallest
    Check(folders[0].Name == "media", "flatlist: largest folder first");
    Check(folders.Any(d => d.Name == "deep"), "flatlist: nested folders included");

    var capped = Reclaim.Core.Scanning.FlatList.Largest(
        flRoot, Reclaim.Core.Scanning.FlatListKind.Files, limit: 2);
    Check(capped.Count == 2 && capped[0].Name == "huge.bin", "flatlist: limit respected");

    Directory.Delete(fl, recursive: true);
}

// ---- Test 10: cleanup analyzer ---------------------------------------------
{
    var cl = Path.Join(Path.GetTempPath(), "reclaim-cleanup-" + Guid.NewGuid().ToString("N"));
    WriteFile(Path.Join(cl, "NVIDIA", "DXCache", "a.bin"), 5000);
    WriteFile(Path.Join(cl, "NVIDIA", "GLCache", "b.bin"), 2000);
    WriteFile(Path.Join(cl, "Temp", "leftover.tmp"), 3000);
    WriteFile(Path.Join(cl, "Documents", "important.docx"), 8000); // must NOT match
    WriteFile(Path.Join(cl, "Steam", "steamapps", "shadercache", "x.bin"), 4000);
    WriteFile(Path.Join(cl, "RandomApp", "shadercache", "y.bin"), 9000); // no required ancestor

    var clRoot = (await new DirectoryScanner().ScanAsync(cl, new ScanOptions())).Root;

    var rules = new List<CleanupRule>
    {
        new() { Id = "nv", Title = "NVIDIA", Category = CleanupCategory.ShaderCache,
                Safety = SafetyTier.SafeRegenerates, Explanation = "x",
                PathTemplates = [Path.Join(cl, "NVIDIA", "DXCache"), Path.Join(cl, "NVIDIA", "GLCache")] },
        new() { Id = "tmp", Title = "Temp", Category = CleanupCategory.TemporaryFiles,
                Safety = SafetyTier.SafeTransient, Explanation = "x",
                PathTemplates = [Path.Join(cl, "Temp")] },
        new() { Id = "steam", Title = "Steam", Category = CleanupCategory.ShaderCache,
                Safety = SafetyTier.SafeRegenerates, Explanation = "x",
                DirectoryNameMatches = ["shadercache"], RequiredAncestorSegments = ["steamapps"] },
    };

    var report = new CleanupAnalyzer(rules, expandEnvironment: s => s).Analyze(clRoot);

    Check(report.AllFindings.Count == 4, $"cleanup: correct finding count (got {report.AllFindings.Count}, want 4)");
    Check(report.AllFindings.All(f => !f.Path.Contains("important")), "cleanup: never matches user documents");
    Check(report.AllFindings.All(f => !f.Path.Contains("RandomApp")),
        "cleanup: required-ancestor constraint excludes false positive");
    Check(report.AllFindings.Any(f => f.Path.Contains("Steam") && f.Path.Contains("shadercache")),
        "cleanup: directory-name match with valid ancestor included");
    Check(report.TotalReclaimableBytes == 5000 + 2000 + 3000 + 4000,
        $"cleanup: total bytes (got {report.TotalReclaimableBytes})");
    Check(report.SafelyReclaimableBytes == report.TotalReclaimableBytes,
        "cleanup: all-safe findings count as safely reclaimable");

    var shaderCat = report.Categories.FirstOrDefault(c => c.Category == CleanupCategory.ShaderCache);
    Check(shaderCat is { LocationCount: 3 }, "cleanup: shader category groups all 3 shader findings");
    Check(report.Categories.Zip(report.Categories.Skip(1)).All(p => p.First.TotalBytes >= p.Second.TotalBytes),
        "cleanup: categories sorted by size descending");

    var cautionRules = new List<CleanupRule>
    {
        new() { Id = "old", Title = "Windows.old", Category = CleanupCategory.SystemMaintenance,
                Safety = SafetyTier.Caution, Explanation = "x", PathTemplates = [Path.Join(cl, "Documents")] },
    };
    var cautionReport = new CleanupAnalyzer(cautionRules, s => s).Analyze(clRoot);
    Check(cautionReport.TotalReclaimableBytes == 8000 && cautionReport.SafelyReclaimableBytes == 0,
        "cleanup: caution-tier excluded from safely-reclaimable total");

    var dupRules = new List<CleanupRule>
    {
        new() { Id = "a", Title = "A", Category = CleanupCategory.TemporaryFiles,
                Safety = SafetyTier.SafeTransient, Explanation = "x", PathTemplates = [Path.Join(cl, "Temp")] },
        new() { Id = "b", Title = "B", Category = CleanupCategory.TemporaryFiles,
                Safety = SafetyTier.SafeTransient, Explanation = "x", PathTemplates = [Path.Join(cl, "Temp")] },
    };
    var dupReport = new CleanupAnalyzer(dupRules, s => s).Analyze(clRoot);
    Check(dupReport.AllFindings.Count == 1, "cleanup: same path not double-counted across rules");

    var emptyReport = new CleanupAnalyzer([], s => s).Analyze(clRoot);
    Check(emptyReport.AllFindings.Count == 0 && emptyReport.TotalReclaimableBytes == 0,
        "cleanup: empty catalog is safe");

    // Sanity: the real bundled catalog loads and is internally well-formed.
    Check(BundledRules.All.Count > 0, "cleanup: bundled catalog is non-empty");
    Check(BundledRules.All.Select(r => r.Id).Distinct().Count() == BundledRules.All.Count,
        "cleanup: bundled rule IDs are unique");
    Check(BundledRules.All.All(r => !string.IsNullOrWhiteSpace(r.Explanation)),
        "cleanup: every bundled rule has an explanation");
    // Every rule must actually be able to match something.
    Check(BundledRules.All.All(r => r.PathTemplates.Count > 0 || r.DirectoryNameMatches.Count > 0),
        "cleanup: every rule has at least one matching strategy");
    // Every rule must carry a title and a defined safety tier.
    Check(BundledRules.All.All(r => !string.IsNullOrWhiteSpace(r.Title)),
        "cleanup: every rule has a title");
    Check(BundledRules.All.All(r => Enum.IsDefined(r.Safety)),
        "cleanup: every rule has a valid safety tier");
    // A name-match rule that could over-match a generic name should pin an ancestor.
    Check(BundledRules.All.All(r =>
            r.DirectoryNameMatches.Count == 0 ||
            r.RequiredAncestorSegments.Count > 0 ||
            r.DirectoryNameMatches.All(n => n.Length > 6)),
        "cleanup: generic name-matches are pinned to an ancestor");

    Directory.Delete(cl, recursive: true);
}

// ---- Test 11: deletion engine safety ---------------------------------------
{
    // Fake remover records calls without touching disk.
    var removed = new List<(string Path, DeletionMode Mode)>();
    var remover = new RecordingRemover(removed);
    var engine = new DeletionEngine(remover);

    // Build a small tree with a cache folder containing files.
    var dl = Path.Join(Path.GetTempPath(), "reclaim-del-" + Guid.NewGuid().ToString("N"));
    WriteFile(Path.Join(dl, "Cache", "a.bin"), 1000);
    WriteFile(Path.Join(dl, "Cache", "b.bin"), 2000);
    var dlRoot = (await new DirectoryScanner().ScanAsync(dl, new ScanOptions())).Root;
    var cacheNode = dlRoot.Children.First(c => c.Name == "Cache");

    CleanupFinding MakeFinding(SafetyTier tier, FileSystemNode node) => new()
    {
        Rule = new CleanupRule { Id = "t", Title = "T", Category = CleanupCategory.BrowserCache,
                                 Safety = tier, Explanation = "x" },
        Node = node,
    };

    // Safe tier → allowed, targets the cache's children, keeps the folder.
    var safeFinding = MakeFinding(SafetyTier.SafeRegenerates, cacheNode);
    var plan = engine.Plan([safeFinding]);
    Check(plan.AllowedCount == 1, "deletion: safe-tier finding is allowed");
    Check(plan.Items[0].Targets.Count == 2, "deletion: targets the folder's contents");
    Check(plan.Items[0].Targets.All(t => t.Path.Contains("Cache")), "deletion: targets stay inside matched folder");
    Check(!plan.Items[0].Targets.Any(t => t.Path.TrimEnd('\\').EndsWith("Cache")),
        "deletion: the matched folder itself is NOT a target (kept)");

    // UseOfficialTool tier → refused, no targets.
    var toolFinding = MakeFinding(SafetyTier.UseOfficialTool, cacheNode);
    var toolPlan = engine.Plan([toolFinding]);
    Check(toolPlan.AllowedCount == 0 && toolPlan.RefusedCount == 1,
        "deletion: UseOfficialTool tier is refused");
    Check(toolPlan.Items[0].RefusalReason == DeletionRefusalReason.TierForbidsDeletion,
        "deletion: refusal reason is tier-forbids");

    // Caution tier → refused.
    var cautionPlan = engine.Plan([MakeFinding(SafetyTier.Caution, cacheNode)]);
    Check(cautionPlan.AllowedCount == 0, "deletion: Caution tier is refused");

    // Execute the safe plan → remover called for both files, folder untouched.
    removed.Clear();
    var delResult = engine.Execute(plan, DeletionMode.RecycleBin);
    Check(removed.Count == 2, $"deletion: remover called per file (got {removed.Count})");
    Check(removed.All(r => r.Mode == DeletionMode.RecycleBin), "deletion: respects RecycleBin mode");
    Check(delResult.Outcomes[0].Succeeded, "deletion: outcome reports success");
    Check(delResult.BytesReclaimed == 3000, "deletion: reclaimed bytes tallied");

    // Accurate accounting: a remover that reports one file in-use frees only the
    // other file's bytes and reports the in-use count.
    var partialEngine = new DeletionEngine(new PartialRemover(inUsePathContains: "a.bin"));
    var partial = partialEngine.Plan([MakeFinding(SafetyTier.SafeRegenerates, cacheNode)]);
    var partialResult = partialEngine.Execute(partial, DeletionMode.RecycleBin);
    Check(partialResult.FilesInUse == 1, "deletion: in-use file counted");
    Check(partialResult.BytesReclaimed == partial.Items[0].Targets
            .First(t => !t.Path.Contains("a.bin")).SizeBytes,
        "deletion: only freed bytes for the file that was actually removed");
    Check(!partialResult.Outcomes[0].Succeeded, "deletion: item with an in-use file is not full success");

    // Permanent mode is honored.
    removed.Clear();
    engine.Execute(plan, DeletionMode.Permanent);
    Check(removed.All(r => r.Mode == DeletionMode.Permanent), "deletion: respects Permanent mode");

    // Executing a refused plan deletes NOTHING.
    removed.Clear();
    engine.Execute(toolPlan, DeletionMode.RecycleBin);
    Check(removed.Count == 0, "deletion: refused items never reach the remover");

    // Protected-path guard: critical roots are always refused.
    Check(DeletionEngine.IsProtectedPath(@"C:\"), "deletion: drive root protected");
    Check(DeletionEngine.IsProtectedPath(@"C:"), "deletion: bare drive protected");
    Check(DeletionEngine.IsProtectedPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        "deletion: user profile root protected");
    Check(!DeletionEngine.IsProtectedPath(Path.Join(dl, "Cache", "a.bin")),
        "deletion: a real cache file is NOT protected");

    // Tier helper is correct.
    Check(DeletionEngine.IsDeletableTier(SafetyTier.SafeRegenerates), "deletion: safe regenerates deletable");
    Check(DeletionEngine.IsDeletableTier(SafetyTier.SafeTransient), "deletion: safe transient deletable");
    Check(!DeletionEngine.IsDeletableTier(SafetyTier.UseOfficialTool), "deletion: tool tier not deletable");
    Check(!DeletionEngine.IsDeletableTier(SafetyTier.Caution), "deletion: caution tier not deletable");

    // ---- explicit single-folder delete (Storage manual management) ----
    removed.Clear();
    var folderToDelete = Path.Join(dl, "Cache");
    Check(DeletionEngine.CanDeleteFolder(folderToDelete), "deletion: a normal folder can be manually deleted");
    Check(!DeletionEngine.CanDeleteFolder(@"C:\Windows\System32"), "deletion: System32 cannot be manually deleted");
    Check(!DeletionEngine.CanDeleteFolder(@"C:\"), "deletion: drive root cannot be manually deleted");

    var didDelete = engine.DeleteFolder(folderToDelete, DeletionMode.RecycleBin);
    Check(didDelete, "deletion: DeleteFolder acts on a normal folder");
    Check(removed.Count == 1 && removed[0].Path == folderToDelete,
        "deletion: DeleteFolder removes the folder itself (not just contents)");

    removed.Clear();
    var refusedDelete = engine.DeleteFolder(@"C:\Windows", DeletionMode.Permanent);
    Check(!refusedDelete && removed.Count == 0,
        "deletion: DeleteFolder refuses a protected root and removes nothing");

    // Structural protection holds on any drive and for key subfolders.
    Check(DeletionEngine.IsProtectedPath(@"D:\Windows"), "deletion: Windows protected on any drive");
    Check(DeletionEngine.IsProtectedPath(@"E:\Program Files"), "deletion: Program Files protected on any drive");
    Check(DeletionEngine.IsProtectedPath(@"C:\Windows\SysWOW64"), "deletion: SysWOW64 protected");
    Check(DeletionEngine.IsProtectedPath(@"C:\Users"), "deletion: Users root protected");
    Check(DeletionEngine.IsProtectedPath(@"C:\$Recycle.Bin"), "deletion: Recycle Bin store protected");
    // But normal user folders deep under these are fine.
    Check(!DeletionEngine.IsProtectedPath(@"C:\Users\me\AppData\Local\Temp"),
        "deletion: a temp folder under Users is NOT protected");
    Check(!DeletionEngine.IsProtectedPath(@"C:\Program Files\MyApp\cache"),
        "deletion: a cache under Program Files is NOT protected");

    // ---- single-file delete (Storage default) ----
    var dl2 = Path.Join(Path.GetTempPath(), "reclaim-del2-" + Guid.NewGuid().ToString("N"));
    WriteFile(Path.Join(dl2, "doc.txt"), 500);
    WriteFile(Path.Join(dl2, "Sub", "x.bin"), 700);
    WriteFile(Path.Join(dl2, "Sub", "y.bin"), 300);
    var dl2Root = (await new DirectoryScanner().ScanAsync(dl2, new ScanOptions())).Root;

    removed.Clear();
    var fileNode = dl2Root.Children.First(c => c.Name == "doc.txt");
    Check(engine.DeleteFile(fileNode.FullPath, DeletionMode.RecycleBin), "deletion: DeleteFile removes a file");
    Check(removed.Count == 1 && removed[0].Path == fileNode.FullPath, "deletion: DeleteFile targets exactly the file");

    // ---- delete folder contents (keeps folder) ----
    removed.Clear();
    var subNode = dl2Root.Children.First(c => c.Name == "Sub");
    var n = engine.DeleteFolderContents(subNode, DeletionMode.RecycleBin);
    Check(n == 2, $"deletion: DeleteFolderContents removes each child (got {n})");
    Check(removed.Count == 2 && removed.All(r => r.Path.Contains("Sub")),
        "deletion: contents targets are the folder's children");
    Check(!removed.Any(r => r.Path.TrimEnd('\\').EndsWith("Sub")),
        "deletion: DeleteFolderContents keeps the folder itself");

    // Contents-delete refuses a protected folder.
    removed.Clear();
    var fakeWin = new FileSystemNode { Name = "Windows", FullPath = @"C:\Windows", IsDirectory = true };
    Check(engine.DeleteFolderContents(fakeWin, DeletionMode.Permanent) == 0 && removed.Count == 0,
        "deletion: DeleteFolderContents refuses a protected folder");

    Directory.Delete(dl2, recursive: true);

    Directory.Delete(dl, recursive: true);
}

// ---- in-memory tree pruning (refresh-after-clean) ----
{
    var pr = Path.Join(Path.GetTempPath(), "reclaim-prune-" + Guid.NewGuid().ToString("N"));
    WriteFile(Path.Join(pr, "A", "f1.bin"), 1000);
    WriteFile(Path.Join(pr, "A", "f2.bin"), 2000);
    WriteFile(Path.Join(pr, "B", "g1.bin"), 500);
    var proot = (await new DirectoryScanner().ScanAsync(pr, new ScanOptions())).Root;
    var rootSize0 = proot.SizeBytes;
    var rootFiles0 = proot.FileCount;
    var a = proot.Children.First(c => c.Name == "A");
    var f1 = a.Children.First(c => c.Name == "f1.bin");

    f1.RemoveFromTree();
    Check(!a.Children.Any(c => c.Name == "f1.bin"), "prune: removed node is gone from parent");
    Check(a.SizeBytes == 2000, "prune: parent size updated");
    Check(proot.SizeBytes == rootSize0 - 1000, "prune: ancestor size propagated");
    Check(proot.FileCount == rootFiles0 - 1, "prune: ancestor file count propagated");
    Check(f1.Parent is null, "prune: detached node has no parent");

    var aSizeBefore = a.SizeBytes;
    a.ClearChildrenFromTree();
    Check(a.Children.Count == 0, "prune: ClearChildren empties the folder");
    Check(a.SizeBytes == 0, "prune: cleared folder size is zero");
    Check(proot.Children.Any(c => c.Name == "A"), "prune: the folder itself is kept");
    Check(proot.SizeBytes == rootSize0 - 1000 - aSizeBefore, "prune: clear propagated to proot");

    Directory.Delete(pr, recursive: true);
}

// ---- file knowledge base ----
{
    FileSystemNode File(string name) => new() { Name = name, FullPath = @"C:\x\" + name, IsDirectory = false };
    FileSystemNode Dir(string name) => new() { Name = name, FullPath = @"C:\x\" + name, IsDirectory = true };

    // Exact filename match.
    var hib = FileKnowledgeBase.Describe(File("hiberfil.sys"));
    Check(hib.IsKnown && hib.Title == "Hibernation file", "knowledge: exact filename matched");
    Check(hib.Safety == RemovalSafety.SystemManaged, "knowledge: hiberfil is system-managed");

    // Folder-name match only applies to directories.
    var nm = FileKnowledgeBase.Describe(Dir("node_modules"));
    Check(nm.IsKnown && nm.Title.Contains("Node"), "knowledge: folder name matched for a directory");
    var nmFile = FileKnowledgeBase.Describe(File("node_modules"));
    Check(!nmFile.IsKnown, "knowledge: folder-name entry does NOT match a file of that name");

    // Extension match only applies to files.
    var log = FileKnowledgeBase.Describe(File("server.log"));
    Check(log.IsKnown && log.Title == "Log file", "knowledge: extension matched for a file");
    var logDir = FileKnowledgeBase.Describe(Dir("server.log"));
    Check(!logDir.IsKnown, "knowledge: extension entry does NOT match a directory");

    // Precedence: exact name beats extension. thumbs.db is an exact entry;
    // a generic .db would (if present) lose to it.
    var thumbs = FileKnowledgeBase.Describe(File("thumbs.db"));
    Check(thumbs.Title == "Thumbnail cache", "knowledge: exact name beats extension");

    // Unknown falls back to generic, not flagged as known.
    var unknown = FileKnowledgeBase.Describe(File("mydata.xyz"));
    Check(!unknown.IsKnown, "knowledge: unknown file gets generic fallback");
    Check(unknown.Title == ".xyz file", "knowledge: generic uses the extension label");
    var unknownDir = FileKnowledgeBase.Describe(Dir("RandomFolder"));
    Check(!unknownDir.IsKnown && unknownDir.Title == "Folder", "knowledge: unknown folder gets generic folder label");

    // Case-insensitive matching.
    var upper = FileKnowledgeBase.Describe(File("HIBERFIL.SYS"));
    Check(upper.IsKnown, "knowledge: matching is case-insensitive");

    // No-extension file doesn't crash and is generic.
    var noext = FileKnowledgeBase.Describe(File("LICENSE"));
    Check(!noext.IsKnown && noext.Title == "File", "knowledge: extensionless file is generic 'File'");

    // ---- location-aware context (the System32 case) ----
    FileSystemNode At(string fullPath, bool dir = false)
    {
        var nm = fullPath.Replace('/', '\\').TrimEnd('\\');
        nm = nm[(nm.LastIndexOf('\\') + 1)..];
        return new FileSystemNode { Name = nm, FullPath = fullPath, IsDirectory = dir };
    }

    var sys32 = FileKnowledgeBase.Describe(At(@"C:\Windows\System32\randomdriver.dll"));
    Check(!sys32.IsKnown, "knowledge: unlisted System32 dll is not 'known'");
    Check(sys32.Title == "Windows system file", "knowledge: System32 file gets system-file context");
    Check(sys32.Safety == RemovalSafety.SystemManaged, "knowledge: System32 file flagged system-managed");
    Check(sys32.Description.Contains("DLL"), "knowledge: System32 .dll context mentions DLL");

    var prog = FileKnowledgeBase.Describe(At(@"C:\Program Files\SomeApp\thing.dat"));
    Check(prog.Title.Contains("Program"), "knowledge: Program Files file gets program context");

    var docs = FileKnowledgeBase.Describe(At(@"C:\Users\me\Documents\notes.xyz"));
    Check(docs.Safety == RemovalSafety.PersonalData, "knowledge: file in Documents is personal data");

    var temp = FileKnowledgeBase.Describe(At(@"C:\Users\me\AppData\Local\Temp\abc.xyz"));
    Check(temp.Safety == RemovalSafety.SafeTransient, "knowledge: file in Temp is transient");

    var winsxs = FileKnowledgeBase.Describe(At(@"C:\Windows\WinSxS\somecomponent", dir: true));
    Check(winsxs.Title.Contains("component store"), "knowledge: WinSxS gets component-store context");

    // Extension-category context for an ordinary location.
    var vid = FileKnowledgeBase.Describe(At(@"D:\stuff\clip.mkv"));
    Check(vid.Title == "Video" && vid.Safety == RemovalSafety.PersonalData,
        "knowledge: mkv in an ordinary place is recognized as video");
}

// ---- duplicate finder ----
{
    var hashCalls = new List<string>();
    var contents = new Dictionary<string, string>();
    var hasher = new MapHasher(contents, hashCalls);

    var dd = Path.Join(Path.GetTempPath(), "reclaim-dup-" + Guid.NewGuid().ToString("N"));
    void Make(string name, int size, string content)
    {
        WriteFile(Path.Join(dd, name), size);
        contents[Path.Join(dd, name)] = content;
    }

    // a,b same size+content (dup); c same size diff content; d unique size;
    // e,f same size+content (second dup group, larger).
    Make("a.txt", 1000, "AAA");
    Make("b.txt", 1000, "AAA");
    Make("c.txt", 1000, "CCC");
    Make("d.txt", 2222, "DDD");
    Make("e.bin", 5000, "EEE");
    Make("f.bin", 5000, "EEE");
    var tree = (await new DirectoryScanner().ScanAsync(dd, new ScanOptions())).Root;

    var finder = new DuplicateFinder(hasher) { MinFileSizeBytes = 1 };
    var report = finder.Find(tree);

    Check(report.Groups.Count == 2, $"dup: found two duplicate groups (got {report.Groups.Count})");
    Check(!hashCalls.Contains(Path.Join(dd, "d.txt")), "dup: unique-size file is never hashed");
    var g1000 = report.Groups.First(g => g.FileSizeBytes == 1000);
    Check(g1000.Files.Count == 2, "dup: a.txt and b.txt grouped, c.txt excluded");
    Check(g1000.ReclaimableBytes == 1000, "dup: reclaimable = (count-1)*size");
    Check(report.Groups[0].FileSizeBytes == 5000, "dup: largest-reclaimable group sorts first");
    Check(report.TotalReclaimableBytes == 1000 + 5000, "dup: total reclaimable summed");

    // Files under the threshold are ignored.
    var rpt2 = new DuplicateFinder(hasher) { MinFileSizeBytes = 4000 }.Find(tree);
    Check(rpt2.Groups.Count == 1 && rpt2.Groups[0].FileSizeBytes == 5000,
        "dup: files under the size threshold are ignored");

    // Unreadable file (hasher throws) is skipped, not fatal.
    contents[Path.Join(dd, "a.txt")] = "BOOM_THROW";
    var rpt3 = new DuplicateFinder(hasher) { MinFileSizeBytes = 1 }.Find(tree);
    Check(rpt3.Groups.All(g => g.Files.All(f => f.Name != "a.txt")),
        "dup: unreadable file is skipped, others still processed");

    Directory.Delete(dd, recursive: true);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) FAILED.");
return failures == 0 ? 0 : 1;

// Fake remover that reports any path containing a marker as "in use", the rest
// as removed — for testing accurate partial-cleanup accounting.
sealed class PartialRemover(string inUsePathContains) : Reclaim.Core.Cleanup.IFileRemover
{
    public Reclaim.Core.Cleanup.RemovalOutcome Remove(string path, Reclaim.Core.Cleanup.DeletionMode mode) =>
        path.Contains(inUsePathContains)
            ? Reclaim.Core.Cleanup.RemovalOutcome.InUse
            : Reclaim.Core.Cleanup.RemovalOutcome.Removed;
}

// Fake remover used by Test 11 — records intended deletions, touches no disk.
sealed class RecordingRemover(System.Collections.Generic.List<(string, Reclaim.Core.Cleanup.DeletionMode)> log)
    : Reclaim.Core.Cleanup.IFileRemover
{
    public Reclaim.Core.Cleanup.RemovalOutcome Remove(string path, Reclaim.Core.Cleanup.DeletionMode mode)
    {
        log.Add((path, mode));
        return Reclaim.Core.Cleanup.RemovalOutcome.Removed;
    }
}

// Fake hasher for the duplicate-finder tests: hashes from an in-memory content
// map and records which paths were hashed. "BOOM_THROW" simulates an unreadable file.
sealed class MapHasher(
    System.Collections.Generic.Dictionary<string, string> contents,
    System.Collections.Generic.List<string> calls)
    : Reclaim.Core.Duplicates.IFileHasher
{
    public string Hash(string fullPath)
    {
        calls.Add(fullPath);
        var content = contents[fullPath];
        if (content == "BOOM_THROW")
            throw new System.IO.IOException("unreadable");
        return content; // content string doubles as its own hash for the test
    }
}
