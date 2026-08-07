using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Llm;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Pipeline;

/// <summary>S4: one component → structured summary + prose (docs/01 §1.7).</summary>
public sealed class ComponentSummarizer(ILlmClient llm, ILogger<ComponentSummarizer> logger)
{
    public sealed record Result(ComponentSummaryDoc Doc, string ProseMd, string StructuredJson);

    private sealed record Response(
        string? Purpose, List<string>? Responsibilities, List<string>? KeyTypes,
        List<string>? ExternalDeps, List<TalksTo>? TalksTo, List<string>? DataFlows,
        List<NotableFile>? NotableFiles, List<string>? Risks, string? ProseMd);

    public async Task<Result> SummarizeAsync(
        string material, IReadOnlySet<string> knownFiles, IReadOnlySet<string> componentNames, CancellationToken ct)
    {
        var call = new JsonLlmCall(llm, logger);
        var response = await call.CallAsync<Response>(
            PromptLibrary.Load(PromptLibrary.ComponentSummary),
            material,
            r =>
            {
                var errors = new List<string>();
                if (string.IsNullOrWhiteSpace(r.Purpose))
                {
                    errors.Add("purpose is required");
                }

                if (string.IsNullOrWhiteSpace(r.ProseMd))
                {
                    errors.Add("proseMd is required");
                }

                return errors;
            },
            maxOutputTokens: 1_600,
            ct);

        // Anti-hallucination: notableFiles must exist; talksTo must reference real components.
        var notable = (response.NotableFiles ?? [])
            .Where(f => f.Path is not null && knownFiles.Contains(f.Path)).ToList();
        var talksTo = (response.TalksTo ?? [])
            .Where(t => t.Component is not null && componentNames.Contains(t.Component)).ToList();

        var doc = new ComponentSummaryDoc(
            response.Purpose!, response.Responsibilities ?? [], response.KeyTypes ?? [],
            response.ExternalDeps ?? [], talksTo, response.DataFlows ?? [], notable, response.Risks ?? []);
        return new Result(doc, response.ProseMd!, JsonSerializer.Serialize(doc, Json.Opts));
    }
}

/// <summary>S5: all component summaries → the architecture backbone.</summary>
public sealed class ArchitectureSynthesizer(ILlmClient llm, ILogger<ArchitectureSynthesizer> logger)
{
    public async Task<ArchitectureDoc> SynthesizeAsync(string material, CancellationToken ct)
    {
        var call = new JsonLlmCall(llm, logger);
        var doc = await call.CallAsync<ArchitectureDoc>(
            PromptLibrary.Load(PromptLibrary.Architecture),
            material,
            Validate,
            maxOutputTokens: 6_000,
            ct);
        return Sanitize(doc);
    }

    private static List<string> Validate(ArchitectureDoc doc)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(doc.OverviewMd))
        {
            errors.Add("overviewMd is required");
        }

        if (doc.Components is not { Count: > 0 })
        {
            errors.Add("components must be non-empty");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in doc.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || !ids.Add(component.Id))
            {
                errors.Add($"component id missing or duplicate: '{component.Id}'");
            }
        }

        return errors;
    }

    /// <summary>Drops edges/steps referencing unknown ids; clamps list sizes.</summary>
    private static ArchitectureDoc Sanitize(ArchitectureDoc doc)
    {
        var ids = doc.Components.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = (doc.Edges ?? [])
            .Where(e => ids.Contains(e.From) && ids.Contains(e.To))
            .Take(40).ToList();
        var scenarios = (doc.Scenarios ?? [])
            .Select(s => s with
            {
                Steps = s.Steps.Where(st => ids.Contains(st.From) && ids.Contains(st.To)).ToList(),
            })
            .Where(s => s.Steps.Count >= 2)
            .Take(4).ToList();
        return doc with { Components = doc.Components.Take(12).ToList(), Edges = edges, Scenarios = scenarios };
    }
}

/// <summary>
/// M10: one scope's material → its internal architecture, in the same ArchitectureDoc
/// shape as S5 so diagrams and section packs reuse unchanged. Tolerates empty
/// sub-summaries (atomic scopes) — the files carry the load there.
/// </summary>
public sealed class ScopeSynthesizer(ILlmClient llm, ILogger<ScopeSynthesizer> logger)
{
    public async Task<ArchitectureDoc> SynthesizeAsync(string material, CancellationToken ct)
    {
        var call = new JsonLlmCall(llm, logger);
        var doc = await call.CallAsync<ArchitectureDoc>(
            PromptLibrary.Load(PromptLibrary.ScopeArchitecture),
            material,
            Validate,
            maxOutputTokens: 6_000,
            ct);
        return Sanitize(doc);
    }

    private static List<string> Validate(ArchitectureDoc doc)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(doc.OverviewMd))
        {
            errors.Add("overviewMd is required");
        }

        if (doc.Components is not { Count: > 0 })
        {
            errors.Add("components must be non-empty");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in doc.Components)
        {
            if (string.IsNullOrWhiteSpace(component.Id) || !ids.Add(component.Id))
            {
                errors.Add($"component id missing or duplicate: '{component.Id}'");
            }
        }

        return errors;
    }

    /// <summary>Scope caps: fewer parts, fewer flows than the repo-level doc.</summary>
    private static ArchitectureDoc Sanitize(ArchitectureDoc doc)
    {
        var ids = doc.Components.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = (doc.Edges ?? [])
            .Where(e => ids.Contains(e.From) && ids.Contains(e.To))
            .Take(24).ToList();
        var scenarios = (doc.Scenarios ?? [])
            .Select(s => s with
            {
                Steps = s.Steps.Where(st => ids.Contains(st.From) && ids.Contains(st.To)).ToList(),
            })
            .Where(s => s.Steps.Count >= 2)
            .Take(3).ToList();
        return doc with { Components = doc.Components.Take(8).ToList(), Edges = edges, Scenarios = scenarios };
    }
}

/// <summary>
/// S6a: architecture (or one scenario) → DiagramSpec, validated through the
/// deterministic renderer — the LLM never writes Mermaid.
/// </summary>
public sealed class DiagramSpecGenerator(ILlmClient llm, ILogger<DiagramSpecGenerator> logger)
{
    public sealed record Result(DiagramSpec Spec, string Mermaid);

    public async Task<Result> GenerateAsync(
        string kind, ArchitectureDoc architecture, ArchScenario? scenario, CancellationToken ct)
    {
        var material = BuildMaterial(kind, architecture, scenario);
        var call = new JsonLlmCall(llm, logger);
        RenderedDiagram? rendered = null;
        var spec = await call.CallAsync<DiagramSpec>(
            PromptLibrary.Load(PromptLibrary.DiagramSpec),
            material,
            s =>
            {
                try
                {
                    rendered = MermaidRenderer.Render(s);
                    return [];
                }
                catch (DiagramValidationException ex)
                {
                    rendered = null;
                    return [ex.Message];
                }
            },
            maxOutputTokens: 2_500,
            ct);
        _ = spec;
        return new Result(rendered!.NormalizedSpec, rendered.Mermaid);
    }

    private static string BuildMaterial(string kind, ArchitectureDoc architecture, ArchScenario? scenario)
    {
        var pack = new PackBuilder(20_000);
        pack.TryAdd("Requested diagram kind", kind);
        pack.TryAdd("Components", string.Join("\n",
            architecture.Components.Select(c => $"- {c.Id}: {c.Name} — {c.Purpose}")));
        if (scenario is null)
        {
            pack.TryAdd("Relationships", string.Join("\n",
                architecture.Edges.Select(e => $"- {e.From} → {e.To}: {e.Label}")));
        }
        else
        {
            pack.TryAdd($"Scenario to diagram: {scenario.Title}", scenario.Description + "\nSteps:\n"
                + string.Join("\n", scenario.Steps.Select((s, i) => $"{i + 1}. {s.From} → {s.To}: {s.Action}")));
        }

        return pack.ToString();
    }
}

/// <summary>
/// S6b: one section's markdown + blocks, with citation tokens resolved against the
/// real file tree — invalid citations are dropped, never invented (docs/01 §1.7).
/// </summary>
public sealed class SectionGenerator(ILlmClient llm, ILogger<SectionGenerator> logger)
{
    public sealed record Result(string Title, string SummaryLine, IReadOnlyList<(string Type, string DataJson)> Blocks, int EstimatedMinutes);

    private sealed record Response(string? Title, string? SummaryLine, string? Markdown, CalloutDto? Callout);

    private sealed record CalloutDto(string? Variant, string? Title, string? Md);

    public async Task<Result> GenerateAsync(
        string workingTitle,
        string material,
        string workspaceRoot,
        RepoMap map,
        DiagramSpecGenerator.Result? diagram,
        CancellationToken ct,
        string? promptName = null)
    {
        var call = new JsonLlmCall(llm, logger);
        var response = await call.CallAsync<Response>(
            PromptLibrary.Load(promptName ?? PromptLibrary.Section),
            $"Working title: {workingTitle}\n{material}",
            r =>
            {
                var errors = new List<string>();
                if (string.IsNullOrWhiteSpace(r.Markdown))
                {
                    errors.Add("markdown is required");
                }

                return errors;
            },
            maxOutputTokens: 2_500,
            ct);

        var blocks = new List<(string Type, string DataJson)>();
        if (diagram is not null)
        {
            blocks.Add((BlockType.Diagram, JsonSerializer.Serialize(new
            {
                diagramKind = diagram.Spec.Kind,
                title = diagram.Spec.Title,
                mermaid = diagram.Mermaid,
                stages = diagram.Spec.Stages.Select(s => new
                {
                    title = s.Title,
                    narrationMd = s.NarrationMd,
                    reveal = new { nodes = s.NodeIds, edges = s.EdgeIndexes },
                }),
            }, Json.Opts)));
        }

        blocks.AddRange(CitationResolver.Resolve(response.Markdown!, workspaceRoot, map, logger));

        if (response.Callout is { Md.Length: > 0 } callout)
        {
            var variant = callout.Variant?.ToLowerInvariant() switch
            {
                "warning" => "warning",
                "convention" => "convention",
                _ => "insight",
            };
            blocks.Add((BlockType.Callout, JsonSerializer.Serialize(
                new { variant, title = callout.Title ?? "Note", md = callout.Md }, Json.Opts)));
        }

        var words = response.Markdown!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var minutes = Math.Max(1, (int)Math.Ceiling(words / 180.0) + (diagram?.Spec.Stages.Count ?? 0));

        return new Result(
            string.IsNullOrWhiteSpace(response.Title) ? workingTitle : response.Title!,
            response.SummaryLine ?? string.Empty,
            blocks,
            minutes);
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
}
