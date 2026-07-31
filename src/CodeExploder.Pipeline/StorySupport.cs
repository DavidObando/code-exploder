using System.Globalization;
using CodeExploder.Domain;

namespace CodeExploder.Pipeline;

/// <summary>Deterministic origin-story helpers (docs/08 §M9): no LLM involved.</summary>
public static class StorySupport
{
    /// <summary>
    /// Renders the repository's life as a Mermaid v11 `timeline` document plus one
    /// narration stage per era. Timeline reveals are narration-only (empty sets) —
    /// the client renders the timeline fully and steps the story text.
    /// </summary>
    public static (string Mermaid, IReadOnlyList<DiagramStage> Stages) RenderTimeline(HistoryDoc history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("timeline");
        sb.AppendLine("    title The life of this repository");

        var stages = new List<DiagramStage>();
        foreach (var era in history.Eras)
        {
            var label = EraLabel(era);
            sb.AppendLine(CultureInfo.InvariantCulture, $"    section {Sanitize(label)}");
            foreach (var moment in era.Moments.Take(4))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"        {moment.At:MMM yyyy} : {Sanitize(moment.Detail ?? moment.Subject)}");
            }

            if (era.Moments.Count == 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"        {era.Start:MMM yyyy} : {era.CommitCount} commits");
            }

            stages.Add(new DiagramStage(
                label,
                $"{era.CommitCount} commits by {string.Join(", ", era.TopAuthors)} touching {era.FilesTouched} files"
                + (era.ComponentsBorn.Count > 0 ? $". Born here: {string.Join(", ", era.ComponentsBorn)}." : "."),
                [], []));
        }

        return (sb.ToString(), stages);
    }

    public static string EraLabel(HistoryEra era) =>
        era.Start.Year == era.End.Year && era.Start.Month == era.End.Month
            ? era.Start.ToString("MMM yyyy", CultureInfo.InvariantCulture)
            : $"{era.Start.ToString("MMM yyyy", CultureInfo.InvariantCulture)} – {era.End.ToString("MMM yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>Material for one story chapter (era).</summary>
    public static string ForEra(
        HistoryDoc history,
        HistoryEra era,
        RepoSummary repoSummary,
        ArchitectureDoc? architecture)
    {
        var pack = new PackBuilder(30_000);
        pack.TryAdd("Repository today", (repoSummary.Description ?? "")
            + $"\nLanguages: {string.Join(", ", repoSummary.Languages.Take(4).Select(l => l.Name))}"
            + $"\nComponents today: {string.Join(", ", repoSummary.Components.Take(10).Select(c => c.Name))}");
        if (architecture is not null)
        {
            pack.TryAdd("Architecture overview (today)", architecture.OverviewMd);
        }

        pack.TryAdd("The whole life (context)",
            $"{history.TotalCommits} commits{(history.Truncated ? " (recent window)" : "")} from "
            + $"{history.FirstCommitAt:yyyy-MM-dd} to {history.LastCommitAt:yyyy-MM-dd} across {history.Eras.Count} eras: "
            + string.Join(" → ", history.Eras.Select(e => $"[{e.Index + 1}] {EraLabel(e)} ({e.CommitCount} commits)")));
        pack.TryAdd("Cast (whole life)", string.Join("\n",
            history.Contributors.Take(8).Select(c => $"- {c.Name}: {c.Commits} commits")));

        pack.TryAdd($"THIS CHAPTER — era {era.Index + 1} of {history.Eras.Count}: {EraLabel(era)}",
            $"{era.CommitCount} commits, {era.FilesTouched} files touched\n"
            + $"Top authors: {string.Join(", ", era.TopAuthors)}\n"
            + (era.ComponentsBorn.Count > 0 ? $"Components born in this era: {string.Join(", ", era.ComponentsBorn)}\n" : ""));
        pack.TryAdd("Moments (your narrative beats)", string.Join("\n", era.Moments.Select(m =>
            $"- [{m.Kind}] {m.At:yyyy-MM-dd} `{m.ShaShort}` by {m.Author}: \"{m.Subject}\" ({m.FilesTouched} files) — {m.Detail}")));

        return pack.ToString();
    }

    private static string Sanitize(string s)
    {
        var cleaned = s.Replace(':', '—').Replace('\n', ' ').Replace('#', ' ').Trim();
        return cleaned.Length <= 60 ? cleaned : cleaned[..60] + "…";
    }
}
