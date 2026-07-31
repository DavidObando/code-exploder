using System.IO.Compression;
using System.Text.Json;
using CodeExploder.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CodeExploder.Storage.Bundles;

/// <summary>
/// Restores a bundle as a ready session for a given user (docs/08 §M7). Ids are
/// re-minted; chunks (with embeddings) go in via binary COPY. Idempotency is the
/// caller's job via <see cref="IsInstalledAsync"/>.
/// </summary>
public sealed class BundleImporter(
    NpgsqlDataSource dataSource,
    SessionStore sessions,
    AnalysisStore analyses,
    ExperienceStore experiences,
    QuizStore quizzes)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static async Task<BundleDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var doc = await JsonSerializer.DeserializeAsync<BundleDocument>(gzip, JsonOpts, ct)
            ?? throw new InvalidOperationException($"bundle deserialized to null: {path}");
        if (doc.Version != BundleDocument.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"bundle {path} has version {doc.Version}; this build reads version {BundleDocument.CurrentVersion}");
        }

        return doc;
    }

    /// <summary>True when this user already has this repo@sha installed.</summary>
    public async Task<bool> IsInstalledAsync(BundleDocument doc, string subject, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select 1
            from sessions s
            join users u on u.id = s.user_id
            join analyses a on a.id = s.analysis_id
            join repos r on r.id = a.repo_id
            where u.subject = $1 and r.owner = $2 and r.name = $3 and a.commit_sha = $4
            limit 1
            """);
        cmd.Parameters.AddWithValue(subject);
        cmd.Parameters.AddWithValue(doc.RepoOwner);
        cmd.Parameters.AddWithValue(doc.RepoName);
        cmd.Parameters.AddWithValue(doc.CommitSha);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<Guid> ImportAsync(BundleDocument doc, string subject, CancellationToken ct = default)
    {
        var userId = await sessions.GetOrCreateUserAsync(subject, subject, ct);
        var repoId = await sessions.GetOrCreateRepoAsync(doc.RepoOwner, doc.RepoName, doc.RepoUrl, ct);
        var analysisId = await sessions.CreateAnalysisAsync(repoId, doc.Kind, doc.PrNumber, ct);

        var readySections = doc.Experience.Sections.Count;
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand(
                """
                update analyses
                set status = $2, commit_sha = $3, plan = $4::jsonb, meta = $5::jsonb,
                    sections_total = $6, sections_ready = $6, finished_at = now()
                where id = $1
                """, conn);
            cmd.Parameters.AddWithValue(analysisId);
            cmd.Parameters.AddWithValue(doc.AnalysisStatus);
            cmd.Parameters.AddWithValue(doc.CommitSha);
            cmd.Parameters.AddWithValue((object?)doc.PlanJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)doc.MetaJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue(readySections);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var fileIds = await analyses.InsertFilesAsync(analysisId, doc.Files.Select(f => new RepoFile(
            f.Path, f.Language, f.SizeBytes, f.Excluded, f.ExcludeReason,
            (FileRole)f.Role, f.Churn, f.Rank)).ToList(), ct);
        await ImportChunksAsync(analysisId, doc.Chunks, fileIds, ct);

        await analyses.InsertComponentsAsync(analysisId, doc.Components.Select(c => new Component(
            c.Name, c.RootPaths, [], [])).ToList(), ct);
        var componentIds = (await analyses.GetComponentsAsync(analysisId, ct))
            .ToDictionary(c => c.Name, c => c.Id, StringComparer.Ordinal);

        foreach (var summary in doc.Summaries)
        {
            await analyses.InsertSummaryAsync(
                analysisId, summary.Scope,
                summary.ComponentName is { } cn ? componentIds.GetValueOrDefault(cn) : null,
                summary.ProseMd, summary.StructuredJson, summary.Model, summary.PromptVersion, ct);
        }

        await ImportSummaryEmbeddingsAsync(analysisId, doc.Summaries, ct);

        var sessionStatus = doc.AnalysisStatus == AnalysisStatus.Ready ? SessionStatus.Ready : SessionStatus.Partial;
        var title = doc.PrNumber is { } pr
            ? $"{doc.RepoOwner}/{doc.RepoName} PR #{pr} (demo)"
            : $"{doc.RepoOwner}/{doc.RepoName} (demo)";
        var sessionId = await sessions.CreateSessionAsync(userId, analysisId, doc.Kind, title, ct);
        await sessions.SetSessionStatusAsync(sessionId, sessionStatus, ct: ct);

        var experienceId = await experiences.CreateExperienceAsync(
            sessionId, doc.Experience.CommitSha, doc.Experience.Model, ct);
        foreach (var section in doc.Experience.Sections)
        {
            var sectionId = await experiences.CreateSectionAsync(
                experienceId, section.Ord, section.Slug, section.Kind, section.Title, "", ct);
            await experiences.CompleteSectionAsync(
                sectionId, section.Summary, section.EstimatedMinutes,
                section.Blocks.Select(b => (b.Type, b.DataJson)).ToList(), ct);
            if (EmbeddingCodec.Decode(section.EmbeddingB64) is { } embedding)
            {
                await experiences.SetSectionEmbeddingAsync(sectionId, embedding, ct);
            }

            if (section.Quiz is { } quiz)
            {
                await quizzes.CreateQuizAsync(
                    sectionId, quiz.Title,
                    quiz.Questions.Select(q => (q.Type, q.Prompt, q.DataJson)).ToList(), ct);
            }
        }

        return sessionId;
    }

    private async Task ImportChunksAsync(
        Guid analysisId, IReadOnlyList<BundleChunk> chunks,
        IReadOnlyDictionary<string, Guid> fileIds, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var import = await conn.BeginBinaryImportAsync(
            """
            copy chunks (id, analysis_id, file_id, start_line, end_line, content, token_count, embedding)
            from stdin (format binary)
            """, ct);
        foreach (var chunk in chunks)
        {
            if (!fileIds.TryGetValue(chunk.FilePath, out var fileId))
            {
                continue;
            }

            await import.StartRowAsync(ct);
            await import.WriteAsync(Guid.NewGuid(), ct);
            await import.WriteAsync(analysisId, ct);
            await import.WriteAsync(fileId, ct);
            await import.WriteAsync(chunk.StartLine, ct);
            await import.WriteAsync(chunk.EndLine, ct);
            await import.WriteAsync(chunk.Content, ct);
            await import.WriteAsync(chunk.TokenCount, ct);
            if (EmbeddingCodec.Decode(chunk.EmbeddingB64) is { } embedding)
            {
                await import.WriteAsync(new Pgvector.Vector(embedding), ct);
            }
            else
            {
                await import.WriteNullAsync(ct);
            }
        }

        await import.CompleteAsync(ct);
    }

    private async Task ImportSummaryEmbeddingsAsync(
        Guid analysisId, IReadOnlyList<BundleSummary> summaries, CancellationToken ct)
    {
        // Summaries were inserted without embeddings; restore them by (scope, prose) match —
        // unique enough within one analysis.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var summary in summaries)
        {
            if (EmbeddingCodec.Decode(summary.EmbeddingB64) is not { } embedding)
            {
                continue;
            }

            await using var cmd = new NpgsqlCommand(
                """
                update summaries set embedding = $3
                where analysis_id = $1 and scope = $2 and prose_md = $4 and embedding is null
                """, conn);
            cmd.Parameters.AddWithValue(analysisId);
            cmd.Parameters.AddWithValue(summary.Scope);
            cmd.Parameters.AddWithValue(new Pgvector.Vector(embedding));
            cmd.Parameters.AddWithValue(summary.ProseMd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
