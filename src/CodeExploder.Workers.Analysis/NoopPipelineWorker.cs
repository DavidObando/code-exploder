using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Storage;

namespace CodeExploder.Workers.Analysis;

/// <summary>
/// The M0 walking-skeleton pipeline (docs/08-milestones-and-risks.md): fake stages that
/// exercise the whole nervous system end-to-end — queue, counting join, event bus,
/// SignalR relay, progress UI. The plan job simulates the deterministic stages, then
/// fans out three section jobs joined by a finalize job, exactly the shape the real
/// pipeline uses from M1 on.
/// </summary>
public sealed class NoopPipelineWorker(
    JobQueue queue,
    SessionStore store,
    ISessionEventBus bus,
    ILogger<NoopPipelineWorker> logger) : BackgroundService
{
    public const string PlanJob = "noop-plan";
    public const string SectionJob = "noop-section";
    public const string FinalizeJob = "noop-finalize";

    private static readonly string[] JobTypes = [PlanJob, SectionJob, FinalizeJob];
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly (int Ordinal, string Title)[] FakeSections =
    [
        (1, "Introduction"),
        (2, "Architecture tour"),
        (3, "Build, test, and release"),
    ];

    private readonly string _workerId = $"worker-analysis:{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{WorkerId} polling for job types: {JobTypes}", _workerId, string.Join(", ", JobTypes));
        while (!stoppingToken.IsCancellationRequested)
        {
            QueuedJob? job = null;
            try
            {
                job = await queue.TryDequeueAsync(JobTypes, _workerId, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                await HandleAsync(job, stoppingToken);
                await queue.CompleteAsync(job.Id, stoppingToken);
                logger.LogInformation("Job {JobId} ({JobType}) completed", job.Id, job.JobType);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} ({JobType}) failed", job?.Id, job?.JobType);
                if (job is not null)
                {
                    await queue.FailAsync(job.Id, ex.Message, CancellationToken.None);
                    if (job.Attempts >= job.MaxAttempts)
                    {
                        await MarkRunFailedAsync(job, ex.Message);
                    }
                }
            }
        }
    }

    private async Task HandleAsync(QueuedJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonOpts)!;
        switch (job.JobType)
        {
            case PlanJob:
                await RunPlanAsync(payload, ct);
                break;
            case SectionJob:
                await RunSectionAsync(payload, ct);
                break;
            case FinalizeJob:
                await RunFinalizeAsync(payload, ct);
                break;
            default:
                throw new InvalidOperationException($"Unhandled job type: {job.JobType}");
        }
    }

    private async Task RunPlanAsync(Payload p, CancellationToken ct)
    {
        await store.SetSessionStatusAsync(p.SessionId, SessionStatus.Analyzing, ct: ct);
        await store.SetAnalysisStatusAsync(p.AnalysisId, AnalysisStatus.Running, ct: ct);

        await SimulateStageAsync(p, AnalysisStages.Clone, [
            "Cloning repository (fake)…",
            "Resolved HEAD to a1b2c3d (fake)",
        ], ct);
        await SimulateStageAsync(p, AnalysisStages.Index, [
            "Indexed 1,204 files (fake)",
            "Skipped 87 vendored files (fake)",
        ], ct);
        await SimulateStageAsync(p, AnalysisStages.Map, [
            "Detected build system: msbuild (fake)",
            "Mapped 12 components (fake)",
        ], ct);

        // Fan-out/join: the finalize job stays blocked until every section job reaches a
        // terminal status — the exact DAG shape the real pipeline uses from M1 on.
        await store.SetSectionsTotalAsync(p.AnalysisId, FakeSections.Length, ct);
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged,
            new { stage = AnalysisStages.Sections, state = StageState.Active });

        var finalizeId = await queue.EnqueueBlockedAsync(
            FinalizeJob, ToJson(p), FakeSections.Length, analysisId: p.AnalysisId, ct: ct);
        foreach (var (ordinal, title) in FakeSections)
        {
            await queue.EnqueueAsync(
                SectionJob,
                ToJson(p with { Ordinal = ordinal, Title = title }),
                analysisId: p.AnalysisId,
                unblocksJobId: finalizeId,
                ct: ct);
        }
    }

    private async Task RunSectionAsync(Payload p, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200 + (p.Ordinal ?? 1) * 500), ct);

        var (ready, total) = await store.IncrementSectionsReadyAsync(p.AnalysisId, ct);
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisNarration,
            new { text = $"Drafted section: {p.Title} (fake)" });
        bus.Publish(p.SessionId, SessionEventKinds.SectionReady,
            new { ordinal = p.Ordinal, title = p.Title });
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisProgress,
            new
            {
                stage = AnalysisStages.Sections,
                percent = total == 0 ? 100 : ready * 100.0 / total,
                detail = $"{ready}/{total} sections",
            });
    }

    private async Task RunFinalizeAsync(Payload p, CancellationToken ct)
    {
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged,
            new { stage = AnalysisStages.Sections, state = StageState.Done });
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged,
            new { stage = AnalysisStages.Finalize, state = StageState.Active });

        await Task.Delay(TimeSpan.FromMilliseconds(800), ct);

        await store.SetAnalysisStatusAsync(p.AnalysisId, AnalysisStatus.Ready, finished: true, ct: ct);
        await store.SetSessionStatusAsync(p.SessionId, SessionStatus.Ready, ct: ct);
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged,
            new { stage = AnalysisStages.Finalize, state = StageState.Done });
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisNarration, new { text = "Analysis complete (fake)." });
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisCompleted, new { });
    }

    private async Task SimulateStageAsync(Payload p, string stage, string[] narration, CancellationToken ct)
    {
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged, new { stage, state = StageState.Active });
        foreach (var percent in new[] { 25, 50, 75, 100 })
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
            bus.Publish(p.SessionId, SessionEventKinds.AnalysisProgress, new { stage, percent = (double)percent });
            var narrationIdx = percent / 50 - 1;
            if (narrationIdx >= 0 && narrationIdx < narration.Length)
            {
                bus.Publish(p.SessionId, SessionEventKinds.AnalysisNarration, new { text = narration[narrationIdx] });
            }
        }

        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged, new { stage, state = StageState.Done });
    }

    private async Task MarkRunFailedAsync(QueuedJob job, string reason)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonOpts)!;
            await store.SetAnalysisStatusAsync(payload.AnalysisId, AnalysisStatus.Failed, reason, finished: true);
            await store.SetSessionStatusAsync(payload.SessionId, SessionStatus.Failed, reason);
            bus.Publish(payload.SessionId, SessionEventKinds.AnalysisFailed, new { reason });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark run failed for job {JobId}", job.Id);
        }
    }

    private static string ToJson(Payload p) => JsonSerializer.Serialize(p, JsonOpts);

    private sealed record Payload(Guid AnalysisId, Guid SessionId, int? Ordinal = null, string? Title = null);
}
