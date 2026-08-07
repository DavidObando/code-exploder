using CodeExploder.Domain;

namespace CodeExploder.Pipeline;

/// <summary>
/// Assembles ranked material into per-stage packs with hard ceilings — pack, don't
/// stuff (docs/06-llm-strategy.md). All content comes from the workspace checkout and
/// prior-stage outputs; nothing is fetched at pack time.
/// </summary>
public static class ContextPacker
{
    public const int SummarizePackChars = 64_000;   // ≈16k tokens
    public const int SynthesizePackChars = 180_000; // ≈45k tokens
    public const int SectionPackChars = 40_000;     // ≈10k tokens
    public const int ScopeSynthesizePackChars = 120_000; // ≈30k tokens — smaller scope, fuller files

    public static string ForComponent(
        string workspaceRoot, RepoMap map, Component component, IReadOnlyList<string> allComponentNames)
    {
        var pack = new PackBuilder(SummarizePackChars);
        pack.TryAdd("Component", $"{component.Name}\nRoot paths: {string.Join(", ", component.RootPaths)}");
        pack.TryAdd("All components in this repository", string.Join(", ", allComponentNames));

        var files = map.Files.Where(f => !f.Excluded).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var ranked = component.FilePaths
            .Where(files.ContainsKey)
            .OrderByDescending(p => files[p].Rank)
            .ToList();

        foreach (var path in ranked.Where(p => files[p].Role.HasFlag(FileRole.Manifest)).Take(2))
        {
            pack.TryAdd($"Manifest: {path}", ReadCapped(workspaceRoot, path, 2_000));
        }

        foreach (var path in ranked.Take(3))
        {
            pack.TryAdd($"File: {path}", ReadCapped(workspaceRoot, path, 6_000));
        }

        foreach (var path in ranked.Skip(3).Take(6))
        {
            pack.TryAdd($"File head: {path}", HeadLines(ReadCapped(workspaceRoot, path, 8_000), 80));
        }

        if (FindReadme(map) is { } readme)
        {
            pack.TryAdd("README excerpt", ReadCapped(workspaceRoot, readme, 4_000));
        }

        pack.TryAdd("All files in component", string.Join("\n", component.FilePaths.Take(200)));
        return pack.ToString();
    }

    public static string ForArchitecture(
        RepoSummary repoSummary,
        IReadOnlyList<(string Component, string ProseMd, string? StructuredJson)> summaries)
    {
        var pack = new PackBuilder(SynthesizePackChars);
        pack.TryAdd("Repository", (repoSummary.Description ?? "(no description)")
            + $"\nLanguages: {string.Join(", ", repoSummary.Languages.Take(5).Select(l => $"{l.Name} {l.Percent:0}%"))}"
            + $"\nBuild systems: {string.Join(", ", repoSummary.BuildSystems)}"
            + $"\nEntry points: {string.Join(", ", repoSummary.EntryPoints.Take(8))}"
            + $"\nCI/CD configs: {string.Join(", ", repoSummary.CiConfigs.Take(8))}");

        foreach (var (component, prose, structured) in summaries)
        {
            pack.TryAdd($"Component summary: {component}", structured ?? prose);
        }

        return pack.ToString();
    }

    public static string ForSection(
        string workspaceRoot,
        RepoMap map,
        RepoSummary repoSummary,
        ArchitectureDoc architecture,
        string kind,
        ArchScenario? scenario,
        DiagramSpec? diagram,
        IReadOnlyList<(string Component, string ProseMd, string? StructuredJson)> summaries)
    {
        var pack = new PackBuilder(SectionPackChars);
        pack.TryAdd("Repository", (repoSummary.Description ?? "")
            + $"\nLanguages: {string.Join(", ", repoSummary.Languages.Take(4).Select(l => l.Name))}"
            + $" | Build: {string.Join(", ", repoSummary.BuildSystems)}");
        pack.TryAdd("Architecture overview", architecture.OverviewMd);

        switch (kind)
        {
            case SectionKind.Intro:
                if (FindReadme(map) is { } readme)
                {
                    pack.TryAdd("README", ReadCapped(workspaceRoot, readme, 8_000));
                }

                pack.TryAdd("Components", string.Join("\n",
                    architecture.Components.Select(c => $"- {c.Name}: {c.Purpose}")));
                break;

            case SectionKind.Architecture:
                pack.TryAdd("Components", string.Join("\n",
                    architecture.Components.Select(c => $"- {c.Name} ({c.Id}): {c.Purpose} — key files: {string.Join(", ", c.KeyFiles.Take(3))}")));
                pack.TryAdd("Relationships", string.Join("\n",
                    architecture.Edges.Select(e => $"- {e.From} → {e.To}: {e.Label}")));
                foreach (var (component, prose, _) in summaries.Take(6))
                {
                    pack.TryAdd($"Component: {component}", prose);
                }

                break;

            case SectionKind.Scenario when scenario is not null:
                pack.TryAdd($"Scenario: {scenario.Title}", scenario.Description + "\nSteps:\n"
                    + string.Join("\n", scenario.Steps.Select((s, i) => $"{i + 1}. {s.From} → {s.To}: {s.Action}")));
                var involved = scenario.Steps.SelectMany(s => new[] { s.From, s.To }).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (component, prose, _) in summaries.Where(s => involved.Contains(s.Component)).Take(4))
                {
                    pack.TryAdd($"Component: {component}", prose);
                }

                break;

            case SectionKind.Build:
                foreach (var ci in repoSummary.CiConfigs.Take(4))
                {
                    pack.TryAdd($"CI/CD config: {ci}", ReadCapped(workspaceRoot, ci, 4_000));
                }

                var manifests = map.Files.Where(f => !f.Excluded && f.Role.HasFlag(FileRole.Manifest))
                    .OrderByDescending(f => f.Rank).Take(4);
                foreach (var manifest in manifests)
                {
                    pack.TryAdd($"Manifest: {manifest.Path}", ReadCapped(workspaceRoot, manifest.Path, 3_000));
                }

                break;

            default:
                break;
        }

        if (diagram is not null)
        {
            pack.TryAdd("Diagram shown above this section (build on it, don't repeat it)",
                $"{diagram.Title}\n" + string.Join("\n", diagram.Stages.Select(s => $"- {s.Title}: {s.NarrationMd}")));
        }

        // Citable material: paths + line counts so the model can cite real ranges.
        var citable = map.Files.Where(f => !f.Excluded)
            .OrderByDescending(f => f.Rank).Take(40)
            .Select(f => $"{f.Path} (lines 1-{CountLines(workspaceRoot, f.Path)})");
        pack.TryAdd("Files you may cite with {{cite:path:start-end}}", string.Join("\n", citable));

        return pack.ToString();
    }

    /// <summary>
    /// M10: material for summarizing one SUB-component during a scope explosion.
    /// Deeper than ForComponent — the scope is small, so more files ride verbatim and
    /// the parent's summary provides orientation instead of the README.
    /// </summary>
    public static string ForSubComponent(
        string workspaceRoot,
        RepoMap map,
        Component subComponent,
        IReadOnlyList<string> siblingNames,
        string parentName,
        string? parentSummaryProse)
    {
        var pack = new PackBuilder(SummarizePackChars);
        pack.TryAdd("Sub-component", $"{subComponent.Name}\nRoot paths: {string.Join(", ", subComponent.RootPaths)}"
            + $"\nThis is one part of the larger component '{parentName}'.");
        pack.TryAdd("Sibling sub-components in this scope", string.Join(", ", siblingNames));
        if (!string.IsNullOrWhiteSpace(parentSummaryProse))
        {
            pack.TryAdd($"Parent component summary: {parentName}", Truncate(parentSummaryProse!, 3_000));
        }

        var files = map.Files.Where(f => !f.Excluded).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var ranked = subComponent.FilePaths
            .Where(files.ContainsKey)
            .OrderByDescending(p => files[p].Rank)
            .ToList();

        foreach (var path in ranked.Where(p => files[p].Role.HasFlag(FileRole.Manifest)).Take(2))
        {
            pack.TryAdd($"Manifest: {path}", ReadCapped(workspaceRoot, path, 2_000));
        }

        foreach (var path in ranked.Take(5))
        {
            pack.TryAdd($"File: {path}", ReadCapped(workspaceRoot, path, 8_000));
        }

        foreach (var path in ranked.Skip(5).Take(8))
        {
            pack.TryAdd($"File head: {path}", HeadLines(ReadCapped(workspaceRoot, path, 10_000), 100));
        }

        pack.TryAdd("All files in sub-component", string.Join("\n", subComponent.FilePaths.Take(200)));
        return pack.ToString();
    }

    /// <summary>
    /// M10: material for synthesizing one scope's internal architecture. The scope is
    /// small, so key files ride verbatim alongside the sub-component summaries — this
    /// is where the dive earns being deeper than the main tour.
    /// </summary>
    public static string ForScopeArchitecture(
        string workspaceRoot,
        RepoMap map,
        Component scope,
        string? scopeSummaryProse,
        string? scopeSummaryStructured,
        IReadOnlyList<(string Component, string ProseMd, string? StructuredJson)> subSummaries)
    {
        var pack = new PackBuilder(ScopeSynthesizePackChars);
        pack.TryAdd("Scope under the microscope",
            $"{scope.Name}\nRoot paths: {string.Join(", ", scope.RootPaths)}\nFiles: {scope.FilePaths.Count}");
        pack.TryAdd($"What the wider tour already said about {scope.Name}",
            scopeSummaryStructured ?? scopeSummaryProse ?? "(no prior summary)");

        foreach (var (component, prose, structured) in subSummaries)
        {
            pack.TryAdd($"Sub-component summary: {component}", structured ?? prose);
        }

        var files = map.Files.Where(f => !f.Excluded).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var ranked = scope.FilePaths
            .Where(files.ContainsKey)
            .OrderByDescending(p => files[p].Rank)
            .ToList();

        // Atomic scopes have no sub-summaries; the files themselves carry the load.
        var verbatim = subSummaries.Count > 0 ? 3 : 6;
        foreach (var path in ranked.Take(verbatim))
        {
            pack.TryAdd($"File: {path}", ReadCapped(workspaceRoot, path, 8_000));
        }

        pack.TryAdd("All files in scope", string.Join("\n", scope.FilePaths.Take(300)));
        return pack.ToString();
    }

    /// <summary>M10: material for one deep-dive child section. Citations are restricted
    /// to the scope's own files so the dive never wanders off-scope.</summary>
    public static string ForScopeSection(
        string workspaceRoot,
        RepoMap map,
        Component scope,
        ArchitectureDoc scopedArchitecture,
        string kind,
        ArchScenario? scenario,
        DiagramSpec? diagram,
        IReadOnlyList<(string Component, string ProseMd, string? StructuredJson)> subSummaries,
        IReadOnlyList<string> repoEdgesTouchingScope)
    {
        var pack = new PackBuilder(SectionPackChars);
        pack.TryAdd("Scope", $"{scope.Name} — a deep dive into one subsystem. The reader has finished the main tour.");
        pack.TryAdd("Scope overview", scopedArchitecture.OverviewMd);

        switch (kind)
        {
            case SectionKind.DeepDiveTour:
                pack.TryAdd("Internal parts", string.Join("\n",
                    scopedArchitecture.Components.Select(c => $"- {c.Name} ({c.Id}): {c.Purpose} — key files: {string.Join(", ", c.KeyFiles.Take(3))}")));
                pack.TryAdd("Internal relationships", string.Join("\n",
                    scopedArchitecture.Edges.Select(e => $"- {e.From} → {e.To}: {e.Label}")));
                foreach (var (component, prose, _) in subSummaries.Take(6))
                {
                    pack.TryAdd($"Sub-component: {component}", prose);
                }

                break;

            case SectionKind.DeepDiveFlow when scenario is not null:
                pack.TryAdd($"Internal flow: {scenario.Title}", scenario.Description + "\nSteps:\n"
                    + string.Join("\n", scenario.Steps.Select((s, i) => $"{i + 1}. {s.From} → {s.To}: {s.Action}")));
                var involved = scenario.Steps.SelectMany(s => new[] { s.From, s.To }).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (component, prose, _) in subSummaries.Where(s => involved.Contains(s.Component)).Take(4))
                {
                    pack.TryAdd($"Sub-component: {component}", prose);
                }

                break;

            case SectionKind.DeepDiveInterfaces:
                pack.TryAdd("How the rest of the system touches this scope",
                    repoEdgesTouchingScope.Count > 0
                        ? string.Join("\n", repoEdgesTouchingScope)
                        : "(no recorded edges — infer the boundary from the files)");
                pack.TryAdd("Internal parts", string.Join("\n",
                    scopedArchitecture.Components.Select(c => $"- {c.Name}: {c.Purpose}")));
                break;

            default:
                break;
        }

        if (diagram is not null)
        {
            pack.TryAdd("Diagram shown above this section (build on it, don't repeat it)",
                $"{diagram.Title}\n" + string.Join("\n", diagram.Stages.Select(s => $"- {s.Title}: {s.NarrationMd}")));
        }

        var files = map.Files.Where(f => !f.Excluded).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var citable = scope.FilePaths
            .Where(files.ContainsKey)
            .OrderByDescending(p => files[p].Rank)
            .Take(40)
            .Select(p => $"{p} (lines 1-{CountLines(workspaceRoot, p)})");
        pack.TryAdd("Files you may cite with {{cite:path:start-end}}", string.Join("\n", citable));

        return pack.ToString();
    }

    /// <summary>Material for the PR-mode section kinds (docs/01 §PR-diff mode).</summary>
    public static string ForPrSection(
        RepoSummary repoSummary,
        ArchitectureDoc architecture,
        string kind,
        string? componentName,
        PrDiff prDiff,
        IReadOnlyList<(string Component, string ProseMd, string? StructuredJson)> summaries)
    {
        var pack = new PackBuilder(SectionPackChars);
        pack.TryAdd("Pull request", $"#{prDiff.PrNumber}: {prDiff.Title ?? "(no title)"}\n"
            + $"Base: {prDiff.BaseRef} | {prDiff.Files.Count} file(s), +{prDiff.TotalAdditions}/−{prDiff.TotalDeletions}\n"
            + (string.IsNullOrWhiteSpace(prDiff.Body) ? "" : "\n" + Truncate(prDiff.Body!, 2_000)));
        pack.TryAdd("Architecture overview", architecture.OverviewMd);

        switch (kind)
        {
            case PrSectionKind.Overview:
                pack.TryAdd("Changed files", string.Join("\n", prDiff.Files.Take(25).Select(f =>
                    $"- {f.Path} ({f.ChangeKind}, +{f.Additions}/−{f.Deletions}{(f.IsTest ? ", test" : "")})")));
                pack.TryAdd("Touched components", string.Join(", ", prDiff.TouchedComponents));
                break;

            case PrSectionKind.Walkthrough when componentName is not null:
                var files = prDiff.Files.Where(f => f.Component == componentName).ToList();
                if (summaries.FirstOrDefault(s => s.Component == componentName) is { ProseMd.Length: > 0 } summary)
                {
                    pack.TryAdd($"Component: {componentName}", summary.ProseMd);
                }

                foreach (var file in files.OrderByDescending(f => f.Additions + f.Deletions).Take(5))
                {
                    pack.TryAdd($"Diff: {file.Path} ({file.ChangeKind}, +{file.Additions}/−{file.Deletions})",
                        string.Join("\n\n", file.Hunks.Select(h => h.Text))
                        + (file.HunksTruncated ? "\n…[more hunks omitted]" : ""));
                }

                break;

            case PrSectionKind.Risk:
                var changedTestComponents = prDiff.Files.Where(f => f.IsTest && f.Component is not null)
                    .Select(f => f.Component!).ToHashSet(StringComparer.Ordinal);
                var untested = prDiff.Files
                    .Where(f => !f.IsTest && f.Component is not null && !changedTestComponents.Contains(f.Component!))
                    .Select(f => f.Path).Take(15).ToList();
                pack.TryAdd("Prod files changed in components with NO test changes",
                    untested.Count > 0 ? string.Join("\n", untested) : "(none — every touched component has test changes)");
                pack.TryAdd("Deleted files", string.Join("\n",
                    prDiff.Files.Where(f => f.ChangeKind == "deleted").Select(f => f.Path).DefaultIfEmpty("(none)")));
                foreach (var file in prDiff.Files.OrderByDescending(f => f.Additions + f.Deletions).Take(2))
                {
                    pack.TryAdd($"Largest change: {file.Path}",
                        string.Join("\n\n", file.Hunks.Take(2).Select(h => h.Text)));
                }

                break;

            default:
                break;
        }

        return pack.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string? FindReadme(RepoMap map) =>
        map.Files.FirstOrDefault(f => !f.Excluded
            && f.Path.Equals("README.md", StringComparison.OrdinalIgnoreCase))?.Path
        ?? map.Files.FirstOrDefault(f => !f.Excluded
            && f.Path.StartsWith("README", StringComparison.OrdinalIgnoreCase))?.Path;

    private static string ReadCapped(string root, string relativePath, int maxChars)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(root, relativePath));
            return text.Length <= maxChars ? text : text[..maxChars] + "\n…[truncated]";
        }
        catch (IOException)
        {
            return "(unreadable)";
        }
    }

    private static string HeadLines(string content, int lines)
    {
        var split = content.Split('\n');
        return split.Length <= lines ? content : string.Join('\n', split.Take(lines)) + "\n…";
    }

    private static int CountLines(string root, string relativePath)
    {
        try
        {
            return File.ReadLines(Path.Combine(root, relativePath)).Count();
        }
        catch (IOException)
        {
            return 1;
        }
    }
}
