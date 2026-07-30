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
