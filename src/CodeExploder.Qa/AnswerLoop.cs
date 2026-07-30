using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeExploder.Domain;
using CodeExploder.Llm;
using CodeExploder.Storage;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Qa;

/// <summary>
/// Single-shot RAG with token streaming (docs/06 §answering): retrieve → pack sources
/// → stream the answer as coalesced transient QaToken events, flushing partial content
/// ~1/s for reconnects and honoring cancellation between flushes. [Sn] markers are
/// post-resolved to structured citations; markers without a source are stripped.
/// </summary>
public sealed partial class AnswerLoop(
    ILlmClient llm,
    IEmbedClient embed,
    Retriever retriever,
    QaStore qaStore,
    ExperienceStore experiences,
    ISessionEventBus bus,
    ILogger<AnswerLoop> logger)
{
    private const int PackChars = 80_000; // ≈20k tokens
    private const int FlushChars = 400;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PartialWriteInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task AnswerAsync(
        Guid analysisId, Guid sessionId, Guid threadId, Guid assistantMessageId, CancellationToken ct)
    {
        var history = await qaStore.ListMessagesAsync(threadId, ct);
        var assistant = history.FirstOrDefault(m => m.Id == assistantMessageId)
            ?? throw new InvalidOperationException("assistant message row missing");
        if (assistant.Status != "streaming")
        {
            return; // cancelled before we started, or an idempotent retry
        }

        var question = history.LastOrDefault(m => m.Role == "user")?.Content
            ?? throw new InvalidOperationException("no user question in thread");

        var questionEmbedding = (await embed.EmbedAsync([question], ct))[0];
        var retrieval = await retriever.RetrieveAsync(analysisId, sessionId, questionEmbedding, question, ct);

        var (material, sources) = BuildMaterial(retrieval, question, history, assistant.SectionContext is { } sc
            ? await experiences.GetSectionTextAsync(sc, ct)
            : null);

        var messages = new List<LlmMessage>
        {
            new("system", LoadPrompt()),
            new("user", material),
        };

        var content = new StringBuilder();
        var pendingFlush = new StringBuilder();
        var seq = 0;
        var lastFlush = DateTimeOffset.UtcNow;
        var lastPartialWrite = DateTimeOffset.UtcNow;
        var cancelled = false;

        try
        {
            await foreach (var delta in llm.ChatStreamAsync(new LlmRequest(messages, MaxOutputTokens: 1_200), ct))
            {
                content.Append(delta);
                pendingFlush.Append(delta);

                var now = DateTimeOffset.UtcNow;
                if (pendingFlush.Length >= FlushChars || now - lastFlush >= FlushInterval)
                {
                    bus.PublishTransient(sessionId, SessionEventKinds.QaToken,
                        new { messageId = assistantMessageId, seq = ++seq, text = pendingFlush.ToString() });
                    pendingFlush.Clear();
                    lastFlush = now;

                    if (now - lastPartialWrite >= PartialWriteInterval)
                    {
                        await qaStore.UpdatePartialContentAsync(assistantMessageId, content.ToString(), ct);
                        lastPartialWrite = now;

                        // The cancel endpoint flips status; stop generating promptly.
                        if (await qaStore.GetStatusAsync(assistantMessageId, ct) == "cancelled")
                        {
                            cancelled = true;
                            break;
                        }
                    }
                }
            }
        }
        catch (LlmException ex)
        {
            logger.LogWarning(ex, "Q&A generation failed for message {MessageId}", assistantMessageId);
            await CompleteAsync(sessionId, threadId, assistantMessageId, "error",
                content.Length > 0 ? content.ToString() : "Sorry — I couldn't reach the model. Try again in a moment.",
                null, ct);
            return;
        }

        if (pendingFlush.Length > 0)
        {
            bus.PublishTransient(sessionId, SessionEventKinds.QaToken,
                new { messageId = assistantMessageId, seq = ++seq, text = pendingFlush.ToString() });
        }

        var (finalText, citations) = ResolveCitations(content.ToString(), sources);
        await CompleteAsync(
            sessionId, threadId, assistantMessageId,
            cancelled ? "cancelled" : "complete",
            finalText,
            citations.Count > 0 ? JsonSerializer.Serialize(citations, JsonOpts) : null,
            ct);
    }

    private async Task CompleteAsync(
        Guid sessionId, Guid threadId, Guid messageId, string status, string content,
        string? citationsJson, CancellationToken ct)
    {
        await qaStore.CompleteMessageAsync(messageId, status, content, citationsJson, null, null, ct);
        bus.Publish(sessionId, SessionEventKinds.QaMessageCompleted, new
        {
            messageId,
            threadId,
            status,
            citations = citationsJson is null
                ? (JsonElement?)null
                : JsonSerializer.Deserialize<JsonElement>(citationsJson),
        });
    }

    private static (string Material, List<RetrievedChunk> Sources) BuildMaterial(
        RetrievalResult retrieval,
        string question,
        IReadOnlyList<QaMessageRow> history,
        (string Title, string Markdown)? currentSection)
    {
        var sb = new StringBuilder();
        var sources = new List<RetrievedChunk>();

        sb.AppendLine("# SOURCES");
        foreach (var prose in retrieval.Prose)
        {
            // Prose sources ground the answer but aren't citable to a file location.
            sb.AppendLine($"\n## Background ({prose.Kind}): {prose.Title}");
            sb.AppendLine(Cap(prose.Text, 4_000));
        }

        foreach (var chunk in retrieval.Chunks)
        {
            if (sb.Length > PackChars)
            {
                break;
            }

            sources.Add(chunk);
            sb.AppendLine($"\n## [S{sources.Count}] {chunk.Path}:{chunk.StartLine}-{chunk.EndLine}");
            sb.AppendLine(Cap(chunk.Content, 6_000));
        }

        if (currentSection is { } section)
        {
            sb.AppendLine($"\n# The learner is currently reading: {section.Title}");
            sb.AppendLine(Cap(section.Markdown, 3_000));
        }

        var turns = history.Where(m => m.Status == "complete").TakeLast(5).ToList();
        if (turns.Count > 1)
        {
            sb.AppendLine("\n# Conversation so far");
            foreach (var turn in turns)
            {
                sb.AppendLine($"{turn.Role}: {Cap(turn.Content, 1_000)}");
            }
        }

        sb.AppendLine($"\n# QUESTION\n{question}");
        return (sb.ToString(), sources);
    }

    /// <summary>Maps [Sn] markers to structured citations; unknown markers are stripped.</summary>
    internal static (string Text, List<Citation> Citations) ResolveCitations(
        string text, IReadOnlyList<RetrievedChunk> sources)
    {
        var cited = new SortedSet<int>();
        var cleaned = MarkerPattern().Replace(text, match =>
        {
            var n = int.Parse(match.Groups[1].Value);
            if (n >= 1 && n <= sources.Count)
            {
                cited.Add(n);
                return match.Value;
            }

            return string.Empty;
        });

        var citations = cited.Select(n => new Citation(
            $"S{n}", sources[n - 1].Path, sources[n - 1].StartLine, sources[n - 1].EndLine,
            sources[n - 1].ChunkId)).ToList();
        return (cleaned, citations);
    }

    public sealed record Citation(string Label, string Path, int StartLine, int EndLine, Guid ChunkId);

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "\n…[truncated]";

    private static string LoadPrompt()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .First(r => r.EndsWith("Prompts.qa.v1.txt", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"\[S(\d{1,2})\]", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex MarkerPattern();
}
