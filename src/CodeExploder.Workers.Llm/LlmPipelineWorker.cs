using System.Text.Json;
using CodeExploder.Analysis;
using CodeExploder.Domain;
using CodeExploder.Llm;
using CodeExploder.Pipeline;
using CodeExploder.Storage;

namespace CodeExploder.Workers.Llm;

/// <summary>
/// The gpu-gen lane (docs/02 §lanes): one poller, concurrency 1, running the LLM
/// stages S4–S6 with progressive publish. Failure policy per job type: a dead
/// component summary leaves a gap (the join proceeds); a dead section marks that
/// section failed and the run ends 'partial'; a dead synthesize fails the run.
/// </summary>
public sealed class LlmPipelineWorker(
    JobQueue queue,
    SessionStore sessions,
    AnalysisStore analyses,
    ExperienceStore experiences,
    ExplosionStore explosions,
    ExplosionLauncher explosionLauncher,
    ExplosionOptions explosionOptions,
    QuizStore quizzes,
    QaStore qaStore,
    CodeExploder.Qa.AnswerLoop answerLoop,
    ISessionEventBus bus,
    ILlmClient llm,
    LlmReadinessGate gate,
    LlmOptions llmOptions,
    IConfiguration config,
    ILoggerFactory loggerFactory,
    ILogger<LlmPipelineWorker> logger) : BackgroundService
{
    private static readonly string[] JobTypes =
    [
        LlmJobTypes.SummarizeComponent, LlmJobTypes.Synthesize, LlmJobTypes.TutorialSection,
        LlmJobTypes.FinalizeExperience, LlmJobTypes.QuizGenerate, LlmJobTypes.GradeQuiz,
        LlmJobTypes.QaAnswer, LlmJobTypes.StorySection,
        LlmJobTypes.SynthesizeScope, LlmJobTypes.FinalizeScope,
    ];

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NotReadyDelay = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _workerId = $"worker-llm:{Environment.MachineName}:{Environment.ProcessId}";
    // Must match the analysis worker's default (shared checkout + repomap artifacts).
    private readonly string _workspacesRoot = config["Workspaces:Root"]
        ?? Path.Combine(Path.GetTempPath(), "code-exploder", "workspaces");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{WorkerId} polling for job types: {JobTypes} (model {Model})",
            _workerId, string.Join(", ", JobTypes), llmOptions.Model);
        while (!stoppingToken.IsCancellationRequested)
        {
            QueuedJob? job = null;
            try
            {
                // Readiness gate: park all LLM work while Ollama is unreachable rather
                // than burning retries (docs/06).
                if (!await gate.IsReadyAsync(stoppingToken))
                {
                    logger.LogWarning("LLM endpoint not ready; parking for {Delay}s", NotReadyDelay.TotalSeconds);
                    await Task.Delay(NotReadyDelay, stoppingToken);
                    continue;
                }

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
                        await HandleTerminalFailureAsync(job, ex.Message);
                    }
                }
            }
        }
    }

    private async Task HandleAsync(QueuedJob job, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonOpts)!;
        switch (job.JobType)
        {
            case LlmJobTypes.SummarizeComponent:
                await RunSummarizeAsync(p, ct);
                break;
            case LlmJobTypes.Synthesize:
                await RunSynthesizeAsync(p, ct);
                break;
            case LlmJobTypes.TutorialSection:
                await RunSectionAsync(p, ct);
                break;
            case LlmJobTypes.FinalizeExperience:
                await RunFinalizeAsync(p, ct);
                break;
            case LlmJobTypes.QuizGenerate:
                await RunQuizGenerateAsync(p, ct);
                break;
            case LlmJobTypes.GradeQuiz:
                await RunGradeQuizAsync(p, ct);
                break;
            case LlmJobTypes.QaAnswer:
                await answerLoop.AnswerAsync(p.AnalysisId, p.SessionId, p.ThreadId!.Value, p.MessageId!.Value, ct);
                break;
            case LlmJobTypes.StorySection:
                await RunStorySectionAsync(p, ct);
                break;
            case LlmJobTypes.SynthesizeScope:
                await RunSynthesizeScopeAsync(p, ct);
                break;
            case LlmJobTypes.FinalizeScope:
                await RunFinalizeScopeAsync(p, ct);
                break;
            default:
                throw new InvalidOperationException($"Unhandled job type: {job.JobType}");
        }
    }

    private async Task RunSummarizeAsync(Payload p, CancellationToken ct)
    {
        if (p.ExplosionId is not null)
        {
            await RunSummarizeSubComponentAsync(p, ct);
            return;
        }

        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var components = new ComponentDetector().Detect(map);
        var component = components.FirstOrDefault(c => c.Name == p.ComponentName)
            ?? throw new InvalidOperationException($"Component not found in map: {p.ComponentName}");

        var material = ContextPacker.ForComponent(
            CheckoutPath(p.AnalysisId), map, component, components.Select(c => c.Name).ToList());
        var knownFiles = map.Files.Where(f => !f.Excluded).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var componentNames = components.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var summarizer = new ComponentSummarizer(llm, LoggerFor<ComponentSummarizer>());
        var result = await summarizer.SummarizeAsync(material, knownFiles, componentNames, ct);

        await analyses.InsertSummaryAsync(
            p.AnalysisId, "component", p.ComponentId, result.ProseMd, result.StructuredJson,
            llmOptions.Model, PromptLibrary.ComponentSummary, ct);

        var done = await analyses.CountComponentSummariesAsync(p.AnalysisId, ct);
        Narrate(p, $"Summarized component: {p.ComponentName}");
        Progress(p, AnalysisStages.Summarize, done * 100.0 / Math.Max(1, components.Count), $"{done}/{components.Count} components");
    }

    /// <summary>M10: summarize ONE sub-component of an exploding scope. No Stage/Progress
    /// emissions — the session is already finalized and its stage bars must not move.</summary>
    private async Task RunSummarizeSubComponentAsync(Payload p, CancellationToken ct)
    {
        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var chain = await BuildNameChainAsync(p.ComponentId!.Value, ct);
        var subComponent = ComponentDetector.ResolveChain(map, chain, explosionOptions.MaxSubComponents)
            ?? throw new InvalidOperationException($"Sub-component not found in map: {string.Join(" / ", chain)}");

        var siblings = (await analyses.GetSubComponentsAsync(p.ScopeComponentId!.Value, ct))
            .Select(c => c.Name).ToList();
        var parentSummary = await analyses.GetComponentSummaryAsync(p.AnalysisId, p.ScopeComponentId.Value, ct);

        var material = ContextPacker.ForSubComponent(
            CheckoutPath(p.AnalysisId), map, subComponent, siblings,
            p.ScopeComponentName ?? chain[0], parentSummary?.ProseMd);
        var knownFiles = map.Files.Where(f => !f.Excluded).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var componentNames = siblings.Append(p.ScopeComponentName ?? string.Empty)
            .Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);

        var summarizer = new ComponentSummarizer(llm, LoggerFor<ComponentSummarizer>());
        var result = await summarizer.SummarizeAsync(material, knownFiles, componentNames, ct);

        await analyses.InsertSummaryAsync(
            p.AnalysisId, "component", p.ComponentId, result.ProseMd, result.StructuredJson,
            llmOptions.Model, PromptLibrary.ComponentSummary, ct);
        Narrate(p, $"Dissected {p.ComponentName}");
    }

    private async Task RunSynthesizeAsync(Payload p, CancellationToken ct)
    {
        Stage(p, AnalysisStages.Summarize, StageState.Done);
        Stage(p, AnalysisStages.Synthesize, StageState.Active);

        var repoSummary = await LoadRepoSummaryAsync(p.AnalysisId, ct);
        var summaries = await analyses.GetComponentSummariesAsync(p.AnalysisId, ct);
        if (summaries.Count == 0)
        {
            throw new InvalidOperationException("No component summaries succeeded; cannot synthesize.");
        }

        var synthesizer = new ArchitectureSynthesizer(llm, LoggerFor<ArchitectureSynthesizer>());
        var architecture = await synthesizer.SynthesizeAsync(
            ContextPacker.ForArchitecture(repoSummary, summaries), ct);

        await analyses.InsertSummaryAsync(
            p.AnalysisId, "repo", null, architecture.OverviewMd,
            JsonSerializer.Serialize(architecture, JsonOpts), llmOptions.Model, PromptLibrary.Architecture, ct);
        Narrate(p, $"Architecture synthesized: {architecture.Components.Count} components, "
            + $"{architecture.Edges.Count} relationships, {architecture.Scenarios.Count} scenarios");

        // Plan the tutorial outline and publish the TOC (sections start pending).
        var commitSha = await analyses.GetCommitShaAsync(p.AnalysisId, ct) ?? "unknown";
        var experienceId = await experiences.CreateExperienceAsync(p.SessionId, commitSha, llmOptions.Model, ct);

        List<(string Slug, string Kind, string Title, string? ScenarioId, string? ComponentName)> outline;
        var prDiff = await TryLoadPrDiffAsync(p.AnalysisId, ct);
        if (prDiff is not null)
        {
            // PR outline (docs/01 §PR-diff mode): overview → walkthroughs ordered
            // bottom-up so dependencies are reviewed before their dependents → risks.
            var groups = PrSupport.OrderBottomUp(
                prDiff.Files.Where(f => f.Component is not null)
                    .GroupBy(f => f.Component!)
                    .OrderByDescending(g => g.Sum(f => f.Additions + f.Deletions))
                    .Take(4).Select(g => g.Key).ToList(),
                architecture);

            outline = [("pr-overview", PrSectionKind.Overview, $"What PR #{prDiff.PrNumber} does", null, null)];
            foreach (var group in groups)
            {
                outline.Add(($"pr-changes-{Slugify(group)}", PrSectionKind.Walkthrough, $"Changes in {group}", null, group));
            }

            outline.Add(("pr-risk", PrSectionKind.Risk, "Risks & review notes", null, null));
        }
        else
        {
            outline =
            [
                ("intro", SectionKind.Intro, "What this is", null, null),
                ("architecture", SectionKind.Architecture, "Architecture tour", null, null),
            ];
            foreach (var scenario in architecture.Scenarios.Take(3))
            {
                outline.Add(($"scenario-{Slugify(scenario.Id)}", SectionKind.Scenario, scenario.Title, scenario.Id, null));
            }

            outline.Add(("build-test-release", SectionKind.Build, "Build, test & release", null, null));
        }

        var sectionJobs = new List<Payload>();
        for (var ord = 0; ord < outline.Count; ord++)
        {
            var (slug, kind, title, scenarioId, componentName) = outline[ord];
            var sectionId = await experiences.CreateSectionAsync(experienceId, ord, slug, kind, title, "", ct);
            sectionJobs.Add(p with
            {
                ExperienceId = experienceId,
                SectionId = sectionId,
                Kind = kind,
                ScenarioId = scenarioId,
                ComponentName = componentName,
                Ordinal = ord,
                Slug = slug,
                Title = title,
            });
        }

        await sessions.SetSectionsTotalAsync(p.AnalysisId, outline.Count, ct);
        Stage(p, AnalysisStages.Synthesize, StageState.Done);
        Stage(p, AnalysisStages.Sections, StageState.Active);
        Narrate(p, $"Planned {outline.Count} tutorial sections; writing…");

        // S9: component summaries + the repo overview embed on the embed lane.
        await queue.EnqueueAsync(
            LlmJobTypes.EmbedSummaries,
            JsonSerializer.Serialize(new { analysisId = p.AnalysisId }, JsonOpts),
            analysisId: p.AnalysisId, ct: ct);

        var finalizeId = await queue.EnqueueBlockedAsync(
            LlmJobTypes.FinalizeExperience,
            JsonSerializer.Serialize(p with { ExperienceId = experienceId }, JsonOpts),
            outline.Count, analysisId: p.AnalysisId, ct: ct);
        foreach (var sectionJob in sectionJobs)
        {
            await queue.EnqueueAsync(
                LlmJobTypes.TutorialSection, JsonSerializer.Serialize(sectionJob, JsonOpts),
                analysisId: p.AnalysisId, unblocksJobId: finalizeId, ct: ct);
        }
    }

    private async Task RunSectionAsync(Payload p, CancellationToken ct)
    {
        if (p.ExplosionId is not null)
        {
            await RunScopeSectionAsync(p, ct);
            return;
        }

        await experiences.SetSectionStatusAsync(p.SectionId!.Value, SectionState.Generating, ct);
        Narrate(p, $"Writing section: {p.Title}");

        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var repoSummary = await LoadRepoSummaryAsync(p.AnalysisId, ct);
        var architecture = JsonSerializer.Deserialize<ArchitectureDoc>(
            await analyses.GetRepoSummaryStructuredAsync(p.AnalysisId, ct)
                ?? throw new InvalidOperationException("architecture doc missing"),
            JsonOpts)!;
        var summaries = await analyses.GetComponentSummariesAsync(p.AnalysisId, ct);
        var scenario = p.ScenarioId is null
            ? null
            : architecture.Scenarios.FirstOrDefault(s => s.Id == p.ScenarioId);

        var prDiff = p.Kind is PrSectionKind.Overview or PrSectionKind.Walkthrough or PrSectionKind.Risk
            ? await TryLoadPrDiffAsync(p.AnalysisId, ct)
            : null;

        // Diagram-bearing kinds get their spec first; a failed diagram degrades to a
        // text-only section rather than failing it (docs/05 §validity guardrail). The
        // PR overview reuses the architecture flowchart with deterministic
        // changed-component badging (docs/01 §PR-diff mode — overlay, no extra LLM).
        DiagramSpecGenerator.Result? diagram = null;
        if (p.Kind is SectionKind.Architecture or SectionKind.Scenario or PrSectionKind.Overview)
        {
            try
            {
                var generator = new DiagramSpecGenerator(llm, LoggerFor<DiagramSpecGenerator>());
                diagram = await generator.GenerateAsync(
                    p.Kind == SectionKind.Scenario ? "sequence" : "flowchart",
                    architecture, scenario, ct);
                if (prDiff is not null && diagram is not null)
                {
                    var badged = MermaidRenderer.Render(
                        PrSupport.BadgeChangedComponents(diagram.Spec, prDiff.TouchedComponents));
                    diagram = new DiagramSpecGenerator.Result(badged.NormalizedSpec, badged.Mermaid);
                }
            }
            catch (GenerationException ex)
            {
                logger.LogWarning(ex, "Diagram generation failed for {Slug}; continuing text-only", p.Slug);
                Narrate(p, $"Diagram for “{p.Title}” didn't validate; continuing without it");
            }
        }

        var material = prDiff is not null
            ? ContextPacker.ForPrSection(repoSummary, architecture, p.Kind!, p.ComponentName, prDiff, summaries)
            : ContextPacker.ForSection(
                CheckoutPath(p.AnalysisId), map, repoSummary, architecture, p.Kind!, scenario, diagram?.Spec, summaries);
        var sectionGenerator = new SectionGenerator(llm, LoggerFor<SectionGenerator>());
        var result = await sectionGenerator.GenerateAsync(
            p.Title!, material, CheckoutPath(p.AnalysisId), map, diagram, ct);

        var blocks = result.Blocks;
        if (prDiff is not null && p.Kind == PrSectionKind.Walkthrough && p.ComponentName is not null)
        {
            blocks = InjectDiffBlocks(blocks, prDiff, p.ComponentName);
        }

        await experiences.CompleteSectionAsync(
            p.SectionId.Value, result.SummaryLine, result.EstimatedMinutes, blocks, ct);

        var (ready, total) = await sessions.IncrementSectionsReadyAsync(p.AnalysisId, ct);
        bus.Publish(p.SessionId, SessionEventKinds.SectionReady,
            new { sectionId = p.SectionId, slug = p.Slug, title = result.Title, ordinal = p.Ordinal, depth = 0, parentSectionId = (Guid?)null });
        Progress(p, AnalysisStages.Sections, total == 0 ? 100 : ready * 100.0 / total, $"{ready}/{total} sections");

        // S7 rides behind the section, non-blocking: quizzes are enhancement, never a
        // gate on tutorial readiness (docs/05 §Quizzes). PR mode gets ONE quiz, on the
        // overview (docs/01 §PR-diff mode).
        if (p.Kind is not (PrSectionKind.Walkthrough or PrSectionKind.Risk))
        {
            await queue.EnqueueAsync(
                LlmJobTypes.QuizGenerate, JsonSerializer.Serialize(p, JsonOpts),
                analysisId: p.AnalysisId, ct: ct);
        }
        await queue.EnqueueAsync(
            LlmJobTypes.EmbedSection,
            JsonSerializer.Serialize(new { sectionId = p.SectionId }, JsonOpts),
            analysisId: p.AnalysisId, ct: ct);
    }

    private async Task RunQuizGenerateAsync(Payload p, CancellationToken ct)
    {
        if (await experiences.GetSectionTextAsync(p.SectionId!.Value, ct) is not { } section
            || string.IsNullOrWhiteSpace(section.Markdown))
        {
            logger.LogInformation("Section {SectionId} has no text; skipping quiz", p.SectionId);
            return;
        }

        var generator = new QuizGenerator(llm, LoggerFor<QuizGenerator>());
        var result = await generator.GenerateAsync(section.Title, section.Markdown, ct);
        var quizId = await quizzes.CreateQuizAsync(p.SectionId.Value, result.Title, result.Questions, ct);
        bus.Publish(p.SessionId, SessionEventKinds.QuizReady, new { sectionId = p.SectionId, quizId });
        Narrate(p, $"Quiz ready for “{section.Title}” ({result.Questions.Count} questions)");
    }

    private async Task RunGradeQuizAsync(Payload p, CancellationToken ct)
    {
        var attempt = await quizzes.GetAttemptAsync(p.AttemptId!.Value, ct)
            ?? throw new InvalidOperationException($"attempt not found: {p.AttemptId}");
        if (attempt.Status == "graded")
        {
            return; // idempotent retry
        }

        var quiz = await quizzes.GetForUserAsync(attempt.QuizId, attempt.UserId, ct)
            ?? throw new InvalidOperationException($"quiz not found: {attempt.QuizId}");

        var answers = JsonSerializer.Deserialize<List<QuizAnswer>>(attempt.AnswersJson, JsonOpts) ?? [];
        var questions = quiz.Questions.Select(q => (q.Id, q.Type, q.DataJson)).ToList();
        var results = QuizGrading.GradeDeterministic(questions, answers);

        for (var i = 0; i < results.Count; i++)
        {
            if (!results[i].NeedsLlm)
            {
                continue;
            }

            var question = quiz.Questions.First(q => q.Id == results[i].QuestionId);
            var data = QuizGrading.ParseData(question.DataJson);
            var answerText = answers.First(a => a.QuestionId == question.Id).Text!;
            var grader = new AnswerGrader(llm, LoggerFor<AnswerGrader>());
            var coverage = await grader.GradeAsync(question.Prompt, data.Rubric!, answerText, ct);
            results[i] = QuizGrading.ResolveShort(question.Id, data, coverage);
        }

        var score = QuizGrading.ComputeScore(results) ?? 0;
        await quizzes.CompleteGradingAsync(
            p.AttemptId.Value, score, JsonSerializer.Serialize(results, JsonOpts), ct);
        await quizzes.RecordScoreAsync(
            attempt.QuizId, attempt.UserId, score, QuizGrading.CompleteThresholdPct, ct);

        if (await quizzes.GetSessionForQuizAsync(attempt.QuizId, ct) is { } route)
        {
            bus.Publish(route.SessionId, SessionEventKinds.QuizGraded,
                new { attemptId = p.AttemptId, quizId = attempt.QuizId, scorePct = score, sectionId = route.SectionId });
        }
    }

    private async Task RunFinalizeAsync(Payload p, CancellationToken ct)
    {
        var unready = await experiences.CountUnreadySectionsAsync(p.ExperienceId!.Value, ct);
        Stage(p, AnalysisStages.Sections, StageState.Done);
        Stage(p, AnalysisStages.Finalize, StageState.Active);

        if (unready > 0)
        {
            await sessions.SetAnalysisStatusAsync(p.AnalysisId, AnalysisStatus.ReadyWithWarnings, finished: true, ct: ct);
            await sessions.SetSessionStatusAsync(p.SessionId, SessionStatus.Partial, ct: ct);
            Narrate(p, $"Tutorial ready with {unready} section(s) unavailable.");
        }
        else
        {
            await sessions.SetAnalysisStatusAsync(p.AnalysisId, AnalysisStatus.Ready, finished: true, ct: ct);
            await sessions.SetSessionStatusAsync(p.SessionId, SessionStatus.Ready, ct: ct);
            Narrate(p, "Tutorial ready. Enjoy the tour!");
        }

        Stage(p, AnalysisStages.Finalize, StageState.Done);
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisCompleted, new { });

        // M10 eager hook: auto-explode the most critical scopes at idle priority.
        // Best-effort by design — eager explosion must never fail finalize.
        try
        {
            await LaunchEagerDivesAsync(p, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Eager deep-dive launch failed for analysis {AnalysisId}", p.AnalysisId);
        }
    }

    /// <summary>Ranks top-level components by criticality and launches the top-K as
    /// eager dives under the architecture section. Repo sessions only; never recurses.</summary>
    private async Task LaunchEagerDivesAsync(Payload p, CancellationToken ct)
    {
        if (explosionOptions.EagerTopK <= 0 || await TryLoadPrDiffAsync(p.AnalysisId, ct) is not null)
        {
            return;
        }

        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var components = new ComponentDetector().Detect(map);
        var summaries = (await analyses.GetComponentSummariesAsync(p.AnalysisId, ct))
            .ToDictionary(s => s.Component, s => s.StructuredJson, StringComparer.Ordinal)
            as IReadOnlyDictionary<string, string?>;
        var ranked = CriticalityScorer.Rank(components, map, summaries, explosionOptions.MinScopeFiles);
        if (ranked.Count == 0)
        {
            return;
        }

        var stored = await analyses.GetTopLevelComponentsAsync(p.AnalysisId, ct);
        var anchorId = await experiences.GetSectionIdBySlugAsync(p.ExperienceId!.Value, "architecture", ct);
        foreach (var (componentName, _) in ranked.Take(explosionOptions.EagerTopK))
        {
            var row = stored.FirstOrDefault(c => string.Equals(c.Name, componentName, StringComparison.Ordinal));
            if (row is null)
            {
                continue;
            }

            var launch = await explosionLauncher.LaunchAsync(new ExplosionRequest(
                p.AnalysisId, p.SessionId, p.ExperienceId.Value,
                row.Id, row.Name,
                ExplosionDepth: 1, ParentExplosionId: null,
                AnchorSectionId: anchorId, SectionDepth: 1,
                ExplosionTrigger.Eager,
                p.Owner ?? string.Empty, p.Name ?? string.Empty, p.GitRef), ct);
            if (launch.Created)
            {
                Narrate(p, $"Queued an automatic deep dive into {row.Name}");
            }
        }
    }

    /// <summary>
    /// M10, the dive's join: synthesize the scope's internal architecture, publish the
    /// deep-dive parent section (overview + diagram) so it's readable early, plan the
    /// child sections, and fan them out joined by finalize-scope. No Stage/Progress —
    /// the session is served; only DeepDive*/SectionReady events flow.
    /// </summary>
    private async Task RunSynthesizeScopeAsync(Payload p, CancellationToken ct)
    {
        var scopeComponentId = p.ScopeComponentId!.Value;
        var deepDiveSectionId = p.DeepDiveSectionId!.Value;
        await experiences.SetSectionStatusAsync(deepDiveSectionId, SectionState.Generating, ct);
        Narrate(p, $"Mapping the inside of {p.ScopeComponentName}…");

        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var chain = await BuildNameChainAsync(scopeComponentId, ct);
        var scope = ComponentDetector.ResolveChain(map, chain, explosionOptions.MaxSubComponents)
            ?? throw new InvalidOperationException($"Scope not found in map: {string.Join(" / ", chain)}");

        var subSummaries = await analyses.GetScopedComponentSummariesAsync(p.AnalysisId, scopeComponentId, ct);
        var ownSummary = await analyses.GetComponentSummaryAsync(p.AnalysisId, scopeComponentId, ct);

        var synthesizer = new ScopeSynthesizer(llm, LoggerFor<ScopeSynthesizer>());
        var architecture = await synthesizer.SynthesizeAsync(
            ContextPacker.ForScopeArchitecture(
                CheckoutPath(p.AnalysisId), map, scope,
                ownSummary?.ProseMd, ownSummary?.StructuredJson, subSummaries),
            ct);

        await analyses.InsertSummaryAsync(
            p.AnalysisId, "scope", scopeComponentId, architecture.OverviewMd,
            JsonSerializer.Serialize(architecture, JsonOpts), llmOptions.Model, PromptLibrary.ScopeArchitecture, ct);

        // The parent section is real content: narration-ready overview + scoped map.
        var blocks = new List<(string Type, string DataJson)>();
        try
        {
            var generator = new DiagramSpecGenerator(llm, LoggerFor<DiagramSpecGenerator>());
            var diagram = await generator.GenerateAsync("flowchart", architecture, null, ct);
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
            }, JsonOpts)));
        }
        catch (GenerationException ex)
        {
            logger.LogWarning(ex, "Scope diagram failed for {Scope}; continuing text-only", p.ScopeComponentName);
        }

        blocks.Insert(0, (BlockType.Markdown, JsonSerializer.Serialize(
            new { md = architecture.OverviewMd }, JsonOpts)));
        await experiences.CompleteSectionAsync(
            deepDiveSectionId, $"Inside {p.ScopeComponentName}", 2, blocks, ct);

        var meta = await experiences.GetSectionMetaAsync(deepDiveSectionId, ct)
            ?? throw new InvalidOperationException("deep-dive section row missing");
        bus.Publish(p.SessionId, SessionEventKinds.SectionReady, new
        {
            sectionId = deepDiveSectionId,
            slug = meta.Slug,
            title = $"Deep dive: {p.ScopeComponentName}",
            ordinal = meta.Ord,
            depth = meta.Depth,
            parentSectionId = meta.ParentSectionId,
        });

        // Plan the children (delete-first keeps a retried dive's slugs stable).
        await experiences.DeleteChildSectionsAsync(deepDiveSectionId, ct);
        var outline = new List<(string Slug, string Kind, string Title, string? ScenarioId)>
        {
            ($"{meta.Slug}-tour", SectionKind.DeepDiveTour, $"How {p.ScopeComponentName} works inside", null),
        };
        var flows = 0;
        foreach (var scenario in architecture.Scenarios)
        {
            if (++flows > 2)
            {
                break;
            }

            outline.Add(($"{meta.Slug}-flow-{flows}", SectionKind.DeepDiveFlow, scenario.Title, scenario.Id));
        }

        outline.Add(($"{meta.Slug}-interfaces", SectionKind.DeepDiveInterfaces,
            $"How {p.ScopeComponentName} talks to the rest", null));
        if (outline.Count > explosionOptions.MaxChildSections)
        {
            outline.RemoveRange(explosionOptions.MaxChildSections, outline.Count - explosionOptions.MaxChildSections);
        }

        var childJobs = new List<Payload>();
        var ord = await experiences.GetNextOrdAsync(meta.ExperienceId, ct);
        foreach (var (slug, kind, title, scenarioId) in outline)
        {
            var sectionId = await experiences.CreateSectionAsync(
                meta.ExperienceId, ord, slug, kind, title, string.Empty,
                meta.Depth + 1, deepDiveSectionId, scopeComponentId, ct);
            childJobs.Add(p with
            {
                ExperienceId = meta.ExperienceId,
                SectionId = sectionId,
                Kind = kind,
                ScenarioId = scenarioId,
                Ordinal = ord,
                Slug = slug,
                Title = title,
            });
            ord++;
        }

        await explosions.SetSectionsTotalAsync(p.ExplosionId!.Value, childJobs.Count, ct);
        Narrate(p, $"Planned {childJobs.Count} deep-dive section(s) for {p.ScopeComponentName}; writing…");

        var priority = p.Priority ?? 0;
        var finalizeId = await queue.EnqueueBlockedAsync(
            LlmJobTypes.FinalizeScope, JsonSerializer.Serialize(p, JsonOpts), childJobs.Count,
            priority, analysisId: p.AnalysisId, ct: ct);
        foreach (var childJob in childJobs)
        {
            await queue.EnqueueAsync(
                LlmJobTypes.TutorialSection, JsonSerializer.Serialize(childJob, JsonOpts),
                priority, analysisId: p.AnalysisId, unblocksJobId: finalizeId, ct: ct);
        }
    }

    /// <summary>M10: one deep-dive child section — scoped architecture, scoped
    /// citations, deeper prompt. Never touches the analyses.sections_* counters.</summary>
    private async Task RunScopeSectionAsync(Payload p, CancellationToken ct)
    {
        await experiences.SetSectionStatusAsync(p.SectionId!.Value, SectionState.Generating, ct);
        Narrate(p, $"Writing deep-dive section: {p.Title}");

        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var scopeComponentId = p.ScopeComponentId!.Value;
        var chain = await BuildNameChainAsync(scopeComponentId, ct);
        var scope = ComponentDetector.ResolveChain(map, chain, explosionOptions.MaxSubComponents)
            ?? throw new InvalidOperationException($"Scope not found in map: {string.Join(" / ", chain)}");

        var architecture = JsonSerializer.Deserialize<ArchitectureDoc>(
            await analyses.GetScopeSummaryStructuredAsync(p.AnalysisId, scopeComponentId, ct)
                ?? throw new InvalidOperationException("scoped architecture doc missing"),
            JsonOpts)!;
        var subSummaries = await analyses.GetScopedComponentSummariesAsync(p.AnalysisId, scopeComponentId, ct);
        var scenario = p.ScenarioId is null
            ? null
            : architecture.Scenarios.FirstOrDefault(s => s.Id == p.ScenarioId);

        DiagramSpecGenerator.Result? diagram = null;
        if (p.Kind == SectionKind.DeepDiveFlow && scenario is not null)
        {
            try
            {
                var generator = new DiagramSpecGenerator(llm, LoggerFor<DiagramSpecGenerator>());
                diagram = await generator.GenerateAsync("sequence", architecture, scenario, ct);
            }
            catch (GenerationException ex)
            {
                logger.LogWarning(ex, "Diagram generation failed for {Slug}; continuing text-only", p.Slug);
            }
        }

        var repoEdges = p.Kind == SectionKind.DeepDiveInterfaces
            ? await LoadRepoEdgesTouchingAsync(p.AnalysisId, chain[0], ct)
            : [];

        var material = ContextPacker.ForScopeSection(
            CheckoutPath(p.AnalysisId), map, scope, architecture, p.Kind!, scenario,
            diagram?.Spec, subSummaries, repoEdges);
        var sectionGenerator = new SectionGenerator(llm, LoggerFor<SectionGenerator>());
        var result = await sectionGenerator.GenerateAsync(
            p.Title!, material, CheckoutPath(p.AnalysisId), map, diagram, ct, PromptLibrary.ScopeSection);

        await experiences.CompleteSectionAsync(
            p.SectionId.Value, result.SummaryLine, result.EstimatedMinutes, result.Blocks, ct);

        await explosions.IncrementSectionsReadyAsync(p.ExplosionId!.Value, ct);
        bus.Publish(p.SessionId, SessionEventKinds.SectionReady, new
        {
            sectionId = p.SectionId,
            slug = p.Slug,
            title = result.Title,
            ordinal = p.Ordinal,
            depth = (p.ExplosionDepth ?? 1) + 1,
            parentSectionId = p.DeepDiveSectionId,
        });

        await queue.EnqueueAsync(
            LlmJobTypes.QuizGenerate, JsonSerializer.Serialize(p, JsonOpts),
            analysisId: p.AnalysisId, ct: ct);
        await queue.EnqueueAsync(
            LlmJobTypes.EmbedSection,
            JsonSerializer.Serialize(new { sectionId = p.SectionId }, JsonOpts),
            analysisId: p.AnalysisId, ct: ct);
    }

    private async Task RunFinalizeScopeAsync(Payload p, CancellationToken ct)
    {
        var explosionId = p.ExplosionId!.Value;
        var unready = await experiences.CountUnreadyChildSectionsAsync(p.DeepDiveSectionId!.Value, ct);
        var status = unready > 0 ? ExplosionStatus.Partial : ExplosionStatus.Ready;
        await explosions.SetStatusAsync(explosionId, status, finished: true, ct: ct);

        var row = await explosions.GetAsync(explosionId, ct);
        bus.Publish(p.SessionId, SessionEventKinds.DeepDiveReady, new
        {
            explosionId,
            componentId = p.ScopeComponentId,
            sectionId = p.DeepDiveSectionId,
            ready = row?.SectionsReady ?? 0,
            total = row?.SectionsTotal ?? 0,
        });
        Narrate(p, unready > 0
            ? $"Deep dive into {p.ScopeComponentName} ready with {unready} section(s) unavailable"
            : $"Deep dive into {p.ScopeComponentName} is ready");
    }

    /// <summary>
    /// Repo-level architecture edges touching the scope's top-level component. The
    /// arch doc's component names/ids are LLM-canonicalized ("Cs2Gs Pipeline",
    /// "cs2gs-pipeline") while the scope name comes from ComponentDetector
    /// ("Cs2Gs.Pipeline"), so match on an alphanumeric-normalized form — a plain
    /// substring check misses on the dot-vs-space difference and the section then
    /// has no cross-boundary material. Edges are rendered with display names.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadRepoEdgesTouchingAsync(
        Guid analysisId, string topLevelName, CancellationToken ct)
    {
        var json = await analyses.GetRepoSummaryStructuredAsync(analysisId, ct);
        if (json is null)
        {
            return [];
        }

        var repoArch = JsonSerializer.Deserialize<ArchitectureDoc>(json, JsonOpts);
        if (repoArch is null)
        {
            return [];
        }

        var target = Normalize(topLevelName);
        var nameById = repoArch.Components.ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);
        var matching = repoArch.Components
            .Where(c =>
            {
                var n = Normalize(c.Name);
                var i = Normalize(c.Id);
                return n.Length > 0 && (n.Contains(target, StringComparison.Ordinal)
                    || target.Contains(n, StringComparison.Ordinal)
                    || i.Contains(target, StringComparison.Ordinal)
                    || target.Contains(i, StringComparison.Ordinal));
            })
            .Select(c => c.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (matching.Count == 0)
        {
            return [];
        }

        string Name(string id) => nameById.GetValueOrDefault(id, id);
        return repoArch.Edges
            .Where(e => matching.Contains(e.From) || matching.Contains(e.To))
            .Select(e => matching.Contains(e.From)
                ? $"- {Name(e.From)} (this scope) → {Name(e.To)}: {e.Label}"
                : $"- {Name(e.From)} → {Name(e.To)} (this scope): {e.Label}")
            .ToList();
    }

    /// <summary>Lowercased, alphanumerics-only — bridges detector names ("A.B") and
    /// LLM-canonicalized arch names/ids ("A B", "a-b").</summary>
    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task MarkDiveFailedAsync(Payload p, string reason)
    {
        if (p.ExplosionId is { } explosionId)
        {
            await explosions.SetStatusAsync(explosionId, ExplosionStatus.Failed, reason, finished: true);
        }

        if (p.DeepDiveSectionId is { } sectionId)
        {
            await experiences.SetSectionStatusAsync(sectionId, SectionState.Failed);
        }

        bus.Publish(p.SessionId, SessionEventKinds.DeepDiveFailed, new
        {
            explosionId = p.ExplosionId,
            componentId = p.ScopeComponentId,
            sectionId = p.DeepDiveSectionId,
            reason,
        });
        Narrate(p, $"Deep dive into {p.ScopeComponentName} failed");
    }

    /// <summary>Walks parent_component_id links into [top-level, …, target] names.</summary>
    private async Task<IReadOnlyList<string>> BuildNameChainAsync(Guid componentId, CancellationToken ct)
    {
        var chain = new List<string>();
        Guid? current = componentId;
        while (current is { } id)
        {
            var row = await analyses.GetComponentAsync(id, ct)
                ?? throw new InvalidOperationException($"Component row missing: {id}");
            chain.Insert(0, row.Name);
            current = row.ParentComponentId;
        }

        return chain;
    }

    private async Task HandleTerminalFailureAsync(QueuedJob job, string reason)
    {
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonOpts)!;
            switch (job.JobType)
            {
                case LlmJobTypes.SummarizeComponent:
                    // Gap tolerated: the synthesize join proceeds with what succeeded.
                    Narrate(p, $"Couldn't summarize component {p.ComponentName}; continuing without it");
                    break;
                case LlmJobTypes.SynthesizeScope:
                    // A dead scope synthesis kills the dive — never the session.
                    await MarkDiveFailedAsync(p, reason);
                    break;
                case LlmJobTypes.FinalizeScope:
                    // Children may have landed; report the dive partial with gap counts.
                    if (p.ExplosionId is { } feExplosionId)
                    {
                        await explosions.SetStatusAsync(feExplosionId, ExplosionStatus.Partial, reason, finished: true);
                        var feRow = await explosions.GetAsync(feExplosionId);
                        bus.Publish(p.SessionId, SessionEventKinds.DeepDiveReady, new
                        {
                            explosionId = feExplosionId,
                            componentId = p.ScopeComponentId,
                            sectionId = p.DeepDiveSectionId,
                            ready = feRow?.SectionsReady ?? 0,
                            total = feRow?.SectionsTotal ?? 0,
                        });
                    }

                    break;
                case LlmJobTypes.TutorialSection or LlmJobTypes.StorySection:
                    await experiences.SetSectionStatusAsync(p.SectionId!.Value, SectionState.Failed);
                    Narrate(p, $"Section “{p.Title}” failed to generate");
                    break;
                case LlmJobTypes.QaAnswer:
                    // A dead answer errors the MESSAGE — the session is already served
                    // and must never flip to failed over a chat turn.
                    await qaStore.CompleteMessageAsync(
                        p.MessageId!.Value, "error",
                        "Sorry — something went wrong answering this. Please try again.",
                        null, null, null);
                    bus.Publish(p.SessionId, SessionEventKinds.QaMessageCompleted,
                        new { messageId = p.MessageId, threadId = p.ThreadId, status = "error", citations = (object?)null });
                    break;
                case LlmJobTypes.QuizGenerate:
                    // Quizzes are enhancement; the section simply has none.
                    Narrate(p, $"Couldn't generate a quiz for “{p.Title}”");
                    break;
                case LlmJobTypes.GradeQuiz:
                    // Grade what the deterministic pass can; the short answer is
                    // excluded (ungradable ≠ wrong), and the attempt resolves.
                    await GradeDeterministicOnlyAsync(p.AttemptId!.Value);
                    break;
                default:
                    await sessions.SetAnalysisStatusAsync(p.AnalysisId, AnalysisStatus.Failed, reason, finished: true);
                    await sessions.SetSessionStatusAsync(p.SessionId, SessionStatus.Failed, reason);
                    bus.Publish(p.SessionId, SessionEventKinds.AnalysisFailed, new { reason });
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle terminal failure for job {JobId}", job.Id);
        }
    }

    /// <summary>
    /// One origin-story chapter (docs/08 §M9): narrate an era from the mined history.
    /// The first chapter opens with the deterministic timeline diagram.
    /// </summary>
    private async Task RunSectionStoryCoreAsync(Payload p, CancellationToken ct)
    {
        await experiences.SetSectionStatusAsync(p.SectionId!.Value, SectionState.Generating, ct);
        Narrate(p, $"Writing {p.Title}");

        var history = JsonSerializer.Deserialize<HistoryDoc>(
            await File.ReadAllTextAsync(
                Path.Combine(_workspacesRoot, p.AnalysisId.ToString(), "history.json"), ct), JsonOpts)
            ?? throw new InvalidOperationException("history artifact missing");
        var era = history.Eras[p.EraIndex!.Value];
        var repoSummary = await LoadRepoSummaryAsync(p.AnalysisId, ct);
        var architectureJson = await analyses.GetRepoSummaryStructuredAsync(p.AnalysisId, ct);
        var architecture = architectureJson is null
            ? null
            : JsonSerializer.Deserialize<ArchitectureDoc>(architectureJson, JsonOpts);

        var map = await LoadRepoMapAsync(p.AnalysisId, ct);
        var material = StorySupport.ForEra(history, era, repoSummary, architecture);
        var generator = new SectionGenerator(llm, LoggerFor<SectionGenerator>());
        var result = await generator.GenerateAsync(
            p.Title!, material, CheckoutPath(p.AnalysisId), map, null, ct, PromptLibrary.Story);

        var blocks = result.Blocks.ToList();
        if (p.EraIndex == 0)
        {
            var (mermaid, stages) = StorySupport.RenderTimeline(history);
            blocks.Insert(0, (BlockType.Diagram, JsonSerializer.Serialize(new
            {
                diagramKind = "timeline",
                title = "The life of this repository",
                mermaid,
                stages = stages.Select(s => new
                {
                    title = s.Title,
                    narrationMd = s.NarrationMd,
                    reveal = new { nodes = s.NodeIds, edges = s.EdgeIndexes },
                }),
            }, JsonOpts)));
        }

        await experiences.CompleteSectionAsync(
            p.SectionId.Value, result.SummaryLine, result.EstimatedMinutes, blocks, ct);

        var (ready, total) = await sessions.IncrementSectionsReadyAsync(p.AnalysisId, ct);
        _ = (ready, total);
        bus.Publish(p.SessionId, SessionEventKinds.SectionReady,
            new { sectionId = p.SectionId, slug = p.Slug, title = result.Title, ordinal = p.Ordinal, depth = 0, parentSectionId = (Guid?)null });

        await queue.EnqueueAsync(
            LlmJobTypes.QuizGenerate, JsonSerializer.Serialize(p, JsonOpts), analysisId: p.AnalysisId, ct: ct);
        await queue.EnqueueAsync(
            LlmJobTypes.EmbedSection,
            JsonSerializer.Serialize(new { sectionId = p.SectionId }, JsonOpts),
            analysisId: p.AnalysisId, ct: ct);
    }

    private Task RunStorySectionAsync(Payload p, CancellationToken ct) => RunSectionStoryCoreAsync(p, ct);

    /// <summary>Diff blocks land right after the section's opening paragraph.</summary>
    private static List<(string Type, string DataJson)> InjectDiffBlocks(
        IReadOnlyList<(string Type, string DataJson)> blocks, PrDiff prDiff, string componentName)
    {
        var diffBlocks = prDiff.Files
            .Where(f => f.Component == componentName && f.Hunks.Count > 0)
            .OrderByDescending(f => f.Additions + f.Deletions)
            .Take(3)
            .SelectMany(f => f.Hunks.Take(2).Select(h => (BlockType.Code, JsonSerializer.Serialize(new
            {
                path = f.Path,
                startLine = h.NewStart,
                endLine = h.NewStart + Math.Max(0, h.NewLines - 1),
                language = "Diff",
                content = h.Text,
                captionMd = $"`{f.ChangeKind}` · +{f.Additions}/−{f.Deletions}"
                    + (f.HunksTruncated ? " · more hunks omitted" : ""),
            }, JsonOpts))))
            .ToList();

        var merged = blocks.ToList();
        var insertAt = merged.FindIndex(b => b.Type == BlockType.Markdown) + 1;
        merged.InsertRange(insertAt > 0 ? insertAt : merged.Count, diffBlocks);
        return merged;
    }

    private async Task<PrDiff?> TryLoadPrDiffAsync(Guid analysisId, CancellationToken ct)
    {
        var path = Path.Combine(_workspacesRoot, analysisId.ToString(), "prdiff.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<PrDiff>(await File.ReadAllTextAsync(path, ct), JsonOpts);
    }

    private async Task GradeDeterministicOnlyAsync(Guid attemptId)
    {
        var attempt = await quizzes.GetAttemptAsync(attemptId);
        if (attempt is null || attempt.Status == "graded")
        {
            return;
        }

        var quiz = await quizzes.GetForUserAsync(attempt.QuizId, attempt.UserId);
        if (quiz is null)
        {
            return;
        }

        var answers = JsonSerializer.Deserialize<List<QuizAnswer>>(attempt.AnswersJson, JsonOpts) ?? [];
        var results = QuizGrading.GradeDeterministic(
            quiz.Questions.Select(q => (q.Id, q.Type, q.DataJson)).ToList(), answers);
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].NeedsLlm)
            {
                var data = QuizGrading.ParseData(quiz.Questions.First(q => q.Id == results[i].QuestionId).DataJson);
                results[i] = QuizGrading.ResolveShort(results[i].QuestionId, data, coverage: null);
            }
        }

        var score = QuizGrading.ComputeScore(results) ?? 0;
        await quizzes.CompleteGradingAsync(attemptId, score, JsonSerializer.Serialize(results, JsonOpts));
        await quizzes.RecordScoreAsync(attempt.QuizId, attempt.UserId, score, QuizGrading.CompleteThresholdPct);
        if (await quizzes.GetSessionForQuizAsync(attempt.QuizId) is { } route)
        {
            bus.Publish(route.SessionId, SessionEventKinds.QuizGraded,
                new { attemptId, quizId = attempt.QuizId, scorePct = score, sectionId = route.SectionId });
        }
    }

    private async Task<RepoMap> LoadRepoMapAsync(Guid analysisId, CancellationToken ct) =>
        JsonSerializer.Deserialize<RepoMap>(
            await File.ReadAllTextAsync(Path.Combine(_workspacesRoot, analysisId.ToString(), "repomap.json"), ct),
            JsonOpts) ?? throw new InvalidOperationException("repo map artifact missing");

    private async Task<RepoSummary> LoadRepoSummaryAsync(Guid analysisId, CancellationToken ct)
    {
        var planJson = await analyses.GetPlanAsync(analysisId, ct)
            ?? throw new InvalidOperationException("analysis plan missing");
        return JsonSerializer.Deserialize<PlanDocument>(planJson, JsonOpts)?.Summary
            ?? throw new InvalidOperationException("repo summary missing from plan");
    }

    private string CheckoutPath(Guid analysisId) =>
        Path.Combine(_workspacesRoot, analysisId.ToString(), "repo");

    private void Stage(Payload p, string stage, string state) =>
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisStageChanged, new { stage, state });

    private void Progress(Payload p, string stage, double percent, string? detail = null) =>
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisProgress, new { stage, percent, detail });

    private void Narrate(Payload p, string text) =>
        bus.Publish(p.SessionId, SessionEventKinds.AnalysisNarration, new { text });

    private ILogger<T> LoggerFor<T>() => loggerFactory.CreateLogger<T>();

    private sealed record PlanDocument(RepoSummary? Summary);

    private sealed record Payload(
        Guid AnalysisId,
        Guid SessionId,
        Guid? ComponentId = null,
        string? ComponentName = null,
        Guid? ExperienceId = null,
        Guid? SectionId = null,
        string? Kind = null,
        string? ScenarioId = null,
        int? Ordinal = null,
        string? Slug = null,
        string? Title = null,
        Guid? AttemptId = null,
        Guid? ThreadId = null,
        Guid? MessageId = null,
        int? EraIndex = null,
        // M10 scope-explosion fields; Owner/Name/GitRef ride through from the analysis
        // payload so the eager hook can hand them to relaunched explode-scope jobs.
        Guid? ExplosionId = null,
        Guid? ScopeComponentId = null,
        string? ScopeComponentName = null,
        Guid? DeepDiveSectionId = null,
        int? ExplosionDepth = null,
        int? Priority = null,
        string? Owner = null,
        string? Name = null,
        string? GitRef = null);

    private static string Slugify(string input) =>
        new(input.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
}
