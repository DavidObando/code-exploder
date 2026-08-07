using System.Text.Json;
using CodeExploder.Domain;
using Npgsql;

namespace CodeExploder.Storage;

/// <summary>Everything a launch needs; callers (gateway, eager finalize hook) resolve
/// these from their own context. SectionDepth is the deep-dive section's depth
/// (anchor's depth + 1); ExplosionDepth is the recursion level (1..MaxDepth).</summary>
public sealed record ExplosionRequest(
    Guid AnalysisId,
    Guid SessionId,
    Guid ExperienceId,
    Guid ComponentId,
    string ComponentName,
    int ExplosionDepth,
    Guid? ParentExplosionId,
    Guid? AnchorSectionId,
    int SectionDepth,
    string Trigger,
    string Owner,
    string RepoName,
    string? GitRef);

public sealed record ExplosionLaunch(ExplosionRow Explosion, bool Created);

/// <summary>
/// M10: the single create-row → create-section → DeepDivePlanned → enqueue sequence,
/// shared by the gateway (on-demand) and the LLM worker's eager hook so the two paths
/// can't drift. Duplicate launches are idempotent; an on-demand request for a dive
/// that's still queued as eager upgrades its priority instead.
/// </summary>
public sealed class ExplosionLauncher(
    ExplosionStore explosions,
    ExperienceStore experiences,
    JobQueue queue,
    ISessionEventBus bus,
    ExplosionOptions options)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ExplosionLaunch> LaunchAsync(ExplosionRequest request, CancellationToken ct = default)
    {
        var priority = PriorityFor(request.Trigger);
        var explosionId = await explosions.TryCreateAsync(
            request.AnalysisId, request.ExperienceId, request.ComponentId,
            request.ParentExplosionId, request.ExplosionDepth, request.Trigger, ct);

        if (explosionId is null)
        {
            // Lost the race or the dive already exists — resolve and maybe upgrade.
            var existing = await explosions.GetByComponentAsync(request.ExperienceId, request.ComponentId, ct)
                ?? throw new InvalidOperationException("Explosion vanished between conflict and fetch.");
            if (existing.Status == ExplosionStatus.Queued
                && existing.Trigger == ExplosionTrigger.Eager
                && request.Trigger == ExplosionTrigger.OnDemand)
            {
                if (existing.QueueJobId is { } jobId)
                {
                    await queue.TrySetPriorityAsync(jobId, priority, ct);
                }

                await explosions.SetTriggerAsync(existing.Id, ExplosionTrigger.OnDemand, ct);
                existing = await explosions.GetAsync(existing.Id, ct) ?? existing;
            }
            else if (existing.Status == ExplosionStatus.Failed
                && request.Trigger == ExplosionTrigger.OnDemand
                && await explosions.ResetForRetryAsync(existing.Id, ExplosionTrigger.OnDemand, ct))
            {
                // Exploding a failed dive again IS the retry (idempotent UX).
                var reset = await explosions.GetAsync(existing.Id, ct) ?? existing;
                await RelaunchAsync(request, reset, ct);
                existing = await explosions.GetAsync(existing.Id, ct) ?? reset;
            }

            return new ExplosionLaunch(existing, Created: false);
        }

        var sectionId = await CreateDeepDiveSectionAsync(request, explosionId.Value, ct);
        await explosions.SetSectionAsync(explosionId.Value, sectionId, ct);
        PublishPlanned(request, explosionId.Value, sectionId);
        await EnqueueExplodeAsync(request, explosionId.Value, sectionId, priority, ct);

        var row = await explosions.GetAsync(explosionId.Value, ct)
            ?? throw new InvalidOperationException("Explosion row missing after create.");
        return new ExplosionLaunch(row, Created: true);
    }

    /// <summary>Relaunches a FAILED dive (caller must have ResetForRetryAsync'd it):
    /// the existing deep-dive section flips back to pending and a fresh explode-scope
    /// job goes out at on-demand priority.</summary>
    public async Task RelaunchAsync(ExplosionRequest request, ExplosionRow row, CancellationToken ct = default)
    {
        var sectionId = row.SectionId ?? await CreateDeepDiveSectionAsync(request, row.Id, ct);
        if (row.SectionId is null)
        {
            await explosions.SetSectionAsync(row.Id, sectionId, ct);
        }

        await experiences.SetSectionStatusAsync(sectionId, SectionState.Pending, ct);
        PublishPlanned(request, row.Id, sectionId);
        await EnqueueExplodeAsync(request, row.Id, sectionId, PriorityFor(ExplosionTrigger.OnDemand), ct);
    }

    private int PriorityFor(string trigger) =>
        trigger == ExplosionTrigger.OnDemand ? options.OnDemandPriority : LlmJobTypes.EagerExplodePriority;

    private async Task<Guid> CreateDeepDiveSectionAsync(
        ExplosionRequest request, Guid explosionId, CancellationToken ct)
    {
        var slug = "dd-" + Slugify(request.ComponentName);
        var title = $"Deep dive: {request.ComponentName}";
        var ord = await experiences.GetNextOrdAsync(request.ExperienceId, ct);
        try
        {
            return await experiences.CreateSectionAsync(
                request.ExperienceId, ord, slug, SectionKind.DeepDive, title, string.Empty,
                request.SectionDepth, request.AnchorSectionId, request.ComponentId, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Slug collision (e.g. two components slugify identically): disambiguate once.
            var unique = $"{slug}-{explosionId.ToString("N")[..6]}";
            return await experiences.CreateSectionAsync(
                request.ExperienceId, ord, unique, SectionKind.DeepDive, title, string.Empty,
                request.SectionDepth, request.AnchorSectionId, request.ComponentId, ct);
        }
    }

    private void PublishPlanned(ExplosionRequest request, Guid explosionId, Guid sectionId) =>
        bus.Publish(request.SessionId, SessionEventKinds.DeepDivePlanned, new
        {
            explosionId,
            componentId = request.ComponentId,
            componentName = request.ComponentName,
            sectionId,
            parentSectionId = request.AnchorSectionId,
            depth = request.SectionDepth,
            trigger = request.Trigger,
        });

    private async Task EnqueueExplodeAsync(
        ExplosionRequest request, Guid explosionId, Guid sectionId, int priority, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            analysisId = request.AnalysisId,
            sessionId = request.SessionId,
            explosionId,
            componentId = request.ComponentId,
            componentName = request.ComponentName,
            explosionDepth = request.ExplosionDepth,
            deepDiveSectionId = sectionId,
            priority,
            owner = request.Owner,
            name = request.RepoName,
            gitRef = request.GitRef,
        }, JsonOpts);
        var jobId = await queue.EnqueueAsync(
            LlmJobTypes.ExplodeScope, payload, priority, analysisId: request.AnalysisId, ct: ct);
        await explosions.SetQueueJobAsync(explosionId, jobId, ct);
    }

    internal static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        }

        return collapsed.Trim('-') is { Length: > 0 } s ? s : "scope";
    }
}
