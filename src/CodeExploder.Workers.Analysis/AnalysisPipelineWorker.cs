using System.Text.Json;
using CodeExploder.Analysis;
using CodeExploder.Domain;
using CodeExploder.GitHub;
using CodeExploder.Storage;

namespace CodeExploder.Workers.Analysis;

/// <summary>
/// The M1 deterministic pipeline (docs/01-analysis-pipeline.md S0–S3): acquire →
/// repo-map → chunk → plan, chained as queue jobs so each stage is retryable and
/// resumable. Narration is real facts from the clone; no LLM is involved until M2.
/// </summary>
public sealed class AnalysisPipelineWorker(
    JobQueue queue,
    SessionStore sessions,
    AnalysisStore analyses,
    ExperienceStore experiences,
    ISessionEventBus bus,
    GitCli git,
    GitHubApiClient gitHubApi,
    IConfiguration config,
    ILogger<AnalysisPipelineWorker> logger) : BackgroundService
{
    public const string AcquireJob = "acquire";
    public const string RepoMapJob = "repo-map";
    public const string ChunkJob = "chunk";
    public const string PlanJob = "plan";

    private static readonly string[] JobTypes =
        [AcquireJob, RepoMapJob, ChunkJob, PlanJob, LlmJobTypes.HistoryMine];
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _workerId = $"worker-analysis:{Environment.MachineName}:{Environment.ProcessId}";
    // Default outside the source tree: a cwd-relative default once left a cloned repo
    // inside a project directory, where its sources glob-compiled into the build.
    private readonly string _workspacesRoot = config["Workspaces:Root"]
        ?? Path.Combine(Path.GetTempPath(), "code-exploder", "workspaces");

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
                    // Acquire failures are user-facing and deterministic (bad repo, too
                    // large) — retrying wastes clones, so they terminally fail at once.
                    var terminal = ex is AcquireException || job.Attempts >= job.MaxAttempts;
                    await queue.FailAsync(job.Id, ex.Message, CancellationToken.None);

                    // Post-completion jobs (the origin story's mine) must never flip a
                    // finished session to failed — the tutorial is already served.
                    if (terminal && job.JobType != LlmJobTypes.HistoryMine)
                    {
                        await MarkRunFailedAsync(job, ex is AcquireException ? ex.Message : "Analysis failed unexpectedly.");
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
            case AcquireJob:
                await RunAcquireAsync(payload, ct);
                break;
            case RepoMapJob:
                await RunRepoMapAsync(payload, ct);
                break;
            case ChunkJob:
                await RunChunkAsync(payload, ct);
                break;
            case PlanJob:
                await RunPlanAsync(payload, ct);
                break;
            case LlmJobTypes.HistoryMine:
                await RunHistoryMineAsync(payload, ct);
                break;
            default:
                throw new InvalidOperationException($"Unhandled job type: {job.JobType}");
        }
    }

    private async Task RunAcquireAsync(Payload p, CancellationToken ct)
    {
        await sessions.SetSessionStatusAsync(p.SessionId, SessionStatus.Analyzing, ct: ct);
        await sessions.SetAnalysisStatusAsync(p.AnalysisId, AnalysisStatus.Running, ct: ct);
        Stage(p, AnalysisStages.Clone, StageState.Active);

        var url = $"https://github.com/{p.Owner}/{p.Name}";
        Narrate(p, p.PrNumber is { } prN
            ? $"Fetching {p.Owner}/{p.Name} pull request #{prN}"
            : $"Cloning {url}" + (p.GitRef is { Length: > 0 } r ? $" @ {r}" : ""));

        // Best-effort API enrichment: description for the vitals card and a size
        // pre-check before spending a clone (docs/01 §S0).
        var info = await gitHubApi.GetRepoAsync(p.Owner, p.Name, ct);
        if (info is not null)
        {
            if (info.SizeKb * 1024 > GitCli.MaxWorkspaceBytes)
            {
                throw new AcquireException(
                    $"Repository is ~{info.SizeKb / 1024} MB on GitHub, over the {GitCli.MaxWorkspaceBytes / (1024 * 1024)} MB limit.");
            }

            if (!string.IsNullOrWhiteSpace(info.Description))
            {
                Narrate(p, $"“{info.Description}”");
            }
        }

        Progress(p, AnalysisStages.Clone, 30);
        var result = await git.CloneAsync(url, CheckoutPath(p.AnalysisId), p.GitRef, p.PrNumber, ct);
        Progress(p, AnalysisStages.Clone, 90);
        Narrate(p, $"Resolved {(p.PrNumber is null ? "HEAD" : "PR head")} to {result.CommitSha[..7]}");

        await analyses.SetCommitShaAsync(
            p.AnalysisId, result.CommitSha, info is null ? null : JsonSerializer.Serialize(info, JsonOpts), ct);

        Stage(p, AnalysisStages.Clone, StageState.Done);
        await queue.EnqueueAsync(RepoMapJob, JsonSerializer.Serialize(p, JsonOpts), analysisId: p.AnalysisId, ct: ct);
    }

    private async Task RunRepoMapAsync(Payload p, CancellationToken ct)
    {
        Stage(p, AnalysisStages.Map, StageState.Active);
        var checkout = CheckoutPath(p.AnalysisId);

        var stats = await git.CollectStatsAsync(checkout, ct);
        Narrate(p, $"History window: {stats.CommitCount} commits by {stats.ContributorCount} contributor(s)");
        Progress(p, AnalysisStages.Map, 30);

        var map = new RepoMapper().Map(checkout, stats);
        Progress(p, AnalysisStages.Map, 70, $"{map.AnalyzedFileCount} files");

        if (map.Languages.Count > 0)
        {
            Narrate(p, "Languages: " + string.Join(", ",
                map.Languages.Take(3).Select(l => $"{l.Name} {l.Percent:0}%")));
        }

        if (map.BuildSystems.Count > 0)
        {
            Narrate(p, "Build systems: " + string.Join(", ", map.BuildSystems));
        }

        if (map.EntryPoints.Count > 0)
        {
            Narrate(p, $"Entry points: {string.Join(", ", map.EntryPoints.Take(3))}"
                + (map.EntryPoints.Count > 3 ? $" (+{map.EntryPoints.Count - 3} more)" : ""));
        }

        Narrate(p, $"Mapped {map.AnalyzedFileCount} analyzable files ({map.ExcludedFileCount} excluded)");

        await analyses.InsertFilesAsync(p.AnalysisId, map.Files, ct);
        await File.WriteAllTextAsync(RepoMapPath(p.AnalysisId), JsonSerializer.Serialize(map, JsonOpts), ct);

        Stage(p, AnalysisStages.Map, StageState.Done);
        await queue.EnqueueAsync(ChunkJob, JsonSerializer.Serialize(p, JsonOpts), analysisId: p.AnalysisId, ct: ct);
    }

    private async Task RunChunkAsync(Payload p, CancellationToken ct)
    {
        Stage(p, AnalysisStages.Index, StageState.Active);
        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var checkout = CheckoutPath(p.AnalysisId);
        var chunker = new Chunker();

        // Re-resolve file ids (repo-map may have retried); chunk inserts are COPY-batched.
        var fileIds = await analyses.InsertFilesAsync(p.AnalysisId, map.Files, ct);

        var analyzable = map.Files.Where(f => !f.Excluded).ToList();
        var chunks = new List<CodeChunk>();
        var processed = 0;
        foreach (var file in analyzable)
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = await File.ReadAllTextAsync(Path.Combine(checkout, file.Path), ct);
            }
            catch (IOException)
            {
                continue;
            }

            chunks.AddRange(chunker.ChunkFile(file.Path, content, file.Language));
            if (++processed % 200 == 0)
            {
                Progress(p, AnalysisStages.Index, processed * 100.0 / analyzable.Count, $"{processed}/{analyzable.Count} files");
            }
        }

        var inserted = await analyses.InsertChunksAsync(p.AnalysisId, fileIds, chunks, ct);
        Narrate(p, $"Chunked {processed} files into {inserted} retrieval chunks");

        // S8 kicks off on the embed lane in parallel with the LLM stages; the batch
        // job is self-draining (docs/01 §S8).
        await queue.EnqueueAsync(
            LlmJobTypes.EmbedBatch,
            JsonSerializer.Serialize(new { analysisId = p.AnalysisId }, JsonOpts),
            analysisId: p.AnalysisId,
            ct: ct);

        Stage(p, AnalysisStages.Index, StageState.Done);
        await queue.EnqueueAsync(PlanJob, JsonSerializer.Serialize(p, JsonOpts), analysisId: p.AnalysisId, ct: ct);
    }

    private async Task RunPlanAsync(Payload p, CancellationToken ct)
    {
        Stage(p, AnalysisStages.Plan, StageState.Active);
        var map = await LoadRepoMapAsync(p.AnalysisId, ct);

        var components = new ComponentDetector().Detect(map);
        await analyses.InsertComponentsAsync(p.AnalysisId, components, ct);
        Narrate(p, $"Detected {components.Count} component(s): "
            + string.Join(", ", components.Take(4).Select(c => c.Name))
            + (components.Count > 4 ? ", …" : ""));

        var chunkCount = await analyses.CountChunksAsync(p.AnalysisId, ct);
        var meta = await analyses.GetMetaAsync(p.AnalysisId, ct);
        var description = meta is null
            ? null
            : JsonSerializer.Deserialize<GitHubRepoInfo>(meta, JsonOpts)?.Description;
        var sha = (await analyses.GetCommitShaAsync(p.AnalysisId, ct)) ?? "unknown";

        var summary = RunPlanner.BuildSummary(sha, description, map, components, chunkCount);
        await analyses.SavePlanAsync(p.AnalysisId, JsonSerializer.Serialize(new { summary }, JsonOpts), ct);
        Stage(p, AnalysisStages.Plan, StageState.Done);

        // PR mode (docs/01 §PR-diff mode): compute the deterministic diff map and scope
        // the S4 fan-out to touched components — the big LLM saving of the PR plan.
        HashSet<string>? scope = null;
        if (p.PrNumber is { } prNumber)
        {
            var prInfo = await gitHubApi.GetPullRequestAsync(p.Owner, p.Name, prNumber, ct);
            var diffText = await git.DiffPrAsync(CheckoutPath(p.AnalysisId), ct);
            var diffFiles = DiffMapper.Map(diffText, map, components);
            var prDiff = new PrDiff(
                prNumber, prInfo?.Title, prInfo?.Body, prInfo?.BaseRef ?? "main", diffFiles);
            await File.WriteAllTextAsync(
                PrDiffPath(p.AnalysisId), JsonSerializer.Serialize(prDiff, JsonOpts), ct);

            var touched = prDiff.TouchedComponents;
            Narrate(p, $"PR #{prNumber} touches {diffFiles.Count} file(s) "
                + $"(+{prDiff.TotalAdditions}/−{prDiff.TotalDeletions}) across {touched.Count} component(s)");

            // Cap by changed volume; docs-only PRs fall back to the top components so
            // the overview still has grounding.
            scope = touched.Count > 0
                ? diffFiles.Where(f => f.Component is not null)
                    .GroupBy(f => f.Component!)
                    .OrderByDescending(g => g.Sum(f => f.Additions + f.Deletions))
                    .Take(8).Select(g => g.Key)
                    .ToHashSet(StringComparer.Ordinal)
                : components.Take(3).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        }

        // Hand off to the gpu-gen lane: S4 fan-out joined by S5 (docs/01 §1.2). The
        // stored component rows carry ids; the LLM worker re-derives file membership
        // deterministically from the repo map.
        var stored = (await analyses.GetComponentsAsync(p.AnalysisId, ct))
            .Where(c => scope is null || scope.Contains(c.Name))
            .ToList();
        Stage(p, AnalysisStages.Summarize, StageState.Active);
        Narrate(p, scope is null
            ? $"Handing off to the expert: summarizing {stored.Count} component(s)…"
            : $"Handing off to the expert: summarizing the {stored.Count} component(s) this PR touches…");

        var synthesizeId = await queue.EnqueueBlockedAsync(
            LlmJobTypes.Synthesize, JsonSerializer.Serialize(p, JsonOpts), stored.Count,
            analysisId: p.AnalysisId, ct: ct);
        foreach (var (componentId, componentName) in stored)
        {
            await queue.EnqueueAsync(
                LlmJobTypes.SummarizeComponent,
                JsonSerializer.Serialize(new
                {
                    analysisId = p.AnalysisId,
                    sessionId = p.SessionId,
                    componentId,
                    componentName,
                }, JsonOpts),
                analysisId: p.AnalysisId,
                unblocksJobId: synthesizeId,
                ct: ct);
        }
    }

    private async Task<RepoMap> LoadRepoMapAsync(Guid analysisId, CancellationToken ct) =>
        JsonSerializer.Deserialize<RepoMap>(await File.ReadAllTextAsync(RepoMapPath(analysisId), ct), JsonOpts)
        ?? throw new InvalidOperationException("repo map artifact missing");

    /// <summary>
    /// The origin story's deterministic pass (docs/08 §M9): unshallow the clone
    /// (bloblessly), mine eras/births/moments from the full log, plan one story
    /// chapter per era on the latest experience, and hand narration to the gpu lane.
    /// </summary>
    private async Task RunHistoryMineAsync(Payload p, CancellationToken ct)
    {
        Narrate(p, "The historian is digging through the archives…");
        var checkout = CheckoutPath(p.AnalysisId);
        if (!Directory.Exists(checkout))
        {
            // The workspace cache may have been reaped; a fresh clone restores it.
            await git.CloneAsync($"https://github.com/{p.Owner}/{p.Name}", checkout, p.GitRef, null, ct);
        }

        await git.UnshallowAsync(checkout, ct);
        var commits = HistoryMiner.ParseLog(await git.FullLogAsync(checkout, HistoryMiner.MaxCommits, ct));

        IReadOnlyList<Component> components;
        if (File.Exists(RepoMapPath(p.AnalysisId)))
        {
            components = new ComponentDetector().Detect(await LoadRepoMapAsync(p.AnalysisId, ct));
        }
        else
        {
            var stats = await git.CollectStatsAsync(checkout, ct);
            var freshMap = new RepoMapper().Map(checkout, stats);
            await File.WriteAllTextAsync(RepoMapPath(p.AnalysisId), JsonSerializer.Serialize(freshMap, JsonOpts), ct);
            components = new ComponentDetector().Detect(freshMap);
        }

        var history = HistoryMiner.Mine(commits, components);
        await File.WriteAllTextAsync(
            HistoryPath(p.AnalysisId), JsonSerializer.Serialize(history, JsonOpts), ct);
        Narrate(p, $"Mined {history.TotalCommits} commits ({history.FirstCommitAt:yyyy-MM-dd} → "
            + $"{history.LastCommitAt:yyyy-MM-dd}) into {history.Eras.Count} era(s)");

        var experience = await experiences.GetLatestForSessionAsync(p.SessionId, ct)
            ?? throw new InvalidOperationException("session has no experience for a story");
        var ord = await experiences.GetNextOrdAsync(experience.Id, ct);
        await sessions.AddSectionsTotalAsync(p.AnalysisId, history.Eras.Count, ct);

        foreach (var era in history.Eras)
        {
            var title = $"Chapter {era.Index + 1}: {CodeExploder.Pipeline.StorySupport.EraLabel(era)}";
            var slug = $"story-era-{era.Index + 1}";
            var sectionId = await experiences.CreateSectionAsync(
                experience.Id, ord, slug, SectionKind.Story, title, "", ct);
            await queue.EnqueueAsync(
                LlmJobTypes.StorySection,
                JsonSerializer.Serialize(new
                {
                    analysisId = p.AnalysisId,
                    sessionId = p.SessionId,
                    experienceId = experience.Id,
                    sectionId,
                    eraIndex = era.Index,
                    ordinal = ord,
                    slug,
                    title,
                }, JsonOpts),
                analysisId: p.AnalysisId,
                ct: ct);
            ord++;
        }
    }

    private string HistoryPath(Guid analysisId) => Path.Combine(_workspacesRoot, analysisId.ToString(), "history.json");

    private string CheckoutPath(Guid analysisId) => Path.Combine(_workspacesRoot, analysisId.ToString(), "repo");

    private string RepoMapPath(Guid analysisId) => Path.Combine(_workspacesRoot, analysisId.ToString(), "repomap.json");

    private string PrDiffPath(Guid analysisId) => Path.Combine(_workspacesRoot, analysisId.ToString(), "prdiff.json");

    private void Stage(Payload p, string stage, string state) =>
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged, new { stage, state });

    private void Progress(Payload p, string stage, double percent, string? detail = null) =>
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisProgress, new { stage, percent, detail });

    private void Narrate(Payload p, string text) =>
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisNarration, new { text });

    private async Task MarkRunFailedAsync(QueuedJob job, string reason)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonOpts)!;
            await sessions.SetAnalysisStatusAsync(payload.AnalysisId, AnalysisStatus.Failed, reason, finished: true);
            await sessions.SetSessionStatusAsync(payload.SessionId, SessionStatus.Failed, reason);
            bus.Publish(payload.SessionId, SessionEventKinds.AnalysisFailed, new { reason });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark run failed for job {JobId}", job.Id);
        }
    }

    public sealed record Payload(
        Guid AnalysisId, Guid SessionId, string Owner, string Name, int? PrNumber, string? GitRef);
}
