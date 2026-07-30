using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Llm;
using CodeExploder.Storage;

namespace CodeExploder.Workers.Llm;

/// <summary>
/// The gpu-embed lane (docs/02 §lanes): a second poller in this process handling
/// embedding batches. embed-batch is self-draining — each job embeds up to one batch
/// of unembedded chunks and re-enqueues itself while any remain, keeping every job
/// under the lease budget without giant payloads.
/// </summary>
public sealed class EmbedLaneWorker(
    JobQueue queue,
    AnalysisStore analyses,
    ExperienceStore experiences,
    IEmbedClient embed,
    ILogger<EmbedLaneWorker> logger) : BackgroundService
{
    public const int BatchSize = 128;

    private static readonly string[] JobTypes =
        [LlmJobTypes.EmbedBatch, LlmJobTypes.EmbedSummaries, LlmJobTypes.EmbedSection];

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _workerId = $"worker-embed:{Environment.MachineName}:{Environment.ProcessId}";

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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embed job {JobId} ({JobType}) failed", job?.Id, job?.JobType);
                if (job is not null)
                {
                    await queue.FailAsync(job.Id, ex.Message, CancellationToken.None);
                }
            }
        }
    }

    private async Task HandleAsync(QueuedJob job, CancellationToken ct)
    {
        var p = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonOpts)!;
        switch (job.JobType)
        {
            case LlmJobTypes.EmbedBatch:
            {
                var batch = await analyses.GetUnembeddedChunksAsync(p.AnalysisId!.Value, BatchSize, ct);
                if (batch.Count == 0)
                {
                    return;
                }

                var vectors = await embed.EmbedAsync(batch.Select(b => Cap(b.Content)).ToList(), ct);
                await analyses.UpdateChunkEmbeddingsAsync(
                    batch.Select((b, i) => (b.Id, vectors[i])).ToList(), ct);

                var (embedded, total) = await analyses.EmbeddingCoverageAsync(p.AnalysisId.Value, ct);
                logger.LogInformation("Embedded {Embedded}/{Total} chunks for {AnalysisId}",
                    embedded, total, p.AnalysisId);
                if (embedded < total)
                {
                    await queue.EnqueueAsync(
                        LlmJobTypes.EmbedBatch, job.PayloadJson, analysisId: p.AnalysisId, ct: ct);
                }

                break;
            }

            case LlmJobTypes.EmbedSummaries:
            {
                var summaries = await analyses.GetUnembeddedSummariesAsync(p.AnalysisId!.Value, ct);
                if (summaries.Count == 0)
                {
                    return;
                }

                var vectors = await embed.EmbedAsync(summaries.Select(s => Cap(s.Text)).ToList(), ct);
                for (var i = 0; i < summaries.Count; i++)
                {
                    await analyses.SetSummaryEmbeddingAsync(summaries[i].Id, vectors[i], ct);
                }

                break;
            }

            case LlmJobTypes.EmbedSection:
            {
                if (await experiences.GetSectionTextAsync(p.SectionId!.Value, ct) is { } section
                    && !string.IsNullOrWhiteSpace(section.Markdown))
                {
                    var vectors = await embed.EmbedAsync([Cap($"{section.Title}\n\n{section.Markdown}")], ct);
                    await experiences.SetSectionEmbeddingAsync(p.SectionId.Value, vectors[0], ct);
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled job type: {job.JobType}");
        }
    }

    // nomic-embed-text's context is ~8k tokens; cap inputs well below it.
    private static string Cap(string s) => s.Length <= 8_000 ? s : s[..8_000];

    private sealed record Payload(Guid? AnalysisId = null, Guid? SectionId = null);
}
