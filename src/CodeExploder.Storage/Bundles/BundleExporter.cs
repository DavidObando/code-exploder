using System.IO.Compression;
using System.Text.Json;
using Npgsql;
using Pgvector;

namespace CodeExploder.Storage.Bundles;

/// <summary>Exports a session's completed analysis + experience as a bundle (docs/08 §M7).</summary>
public sealed class BundleExporter(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task ExportAsync(Guid sessionId, string outputPath, CancellationToken ct = default)
    {
        var doc = await BuildAsync(sessionId, ct);
        await using var file = File.Create(outputPath);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        await JsonSerializer.SerializeAsync(gzip, doc, JsonOpts, ct);
    }

    private async Task<BundleDocument> BuildAsync(Guid sessionId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        Guid analysisId;
        string owner, name, url, kind, commitSha, analysisStatus;
        int? prNumber;
        string? planJson, metaJson;
        await using (var cmd = new NpgsqlCommand(
            """
            select a.id, r.owner, r.name, r.url, a.kind, a.pr_number, a.commit_sha,
                   a.status, a.plan::text, a.meta::text
            from sessions s
            join analyses a on a.id = s.analysis_id
            join repos r on r.id = a.repo_id
            where s.id = $1
            """, conn))
        {
            cmd.Parameters.AddWithValue(sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"session not found: {sessionId}");
            }

            analysisId = reader.GetGuid(0);
            owner = reader.GetString(1);
            name = reader.GetString(2);
            url = reader.GetString(3);
            kind = reader.GetString(4);
            prNumber = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            commitSha = reader.IsDBNull(6) ? "unknown" : reader.GetString(6);
            analysisStatus = reader.GetString(7);
            planJson = reader.IsDBNull(8) ? null : reader.GetString(8);
            metaJson = reader.IsDBNull(9) ? null : reader.GetString(9);
        }

        var files = new List<BundleFile>();
        await using (var cmd = new NpgsqlCommand(
            "select path, language, size_bytes, excluded, exclude_reason, role, churn, rank from files where analysis_id = $1",
            conn))
        {
            cmd.Parameters.AddWithValue(analysisId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                files.Add(new BundleFile(
                    reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)));
            }
        }

        var chunks = new List<BundleChunk>();
        await using (var cmd = new NpgsqlCommand(
            """
            select f.path, c.start_line, c.end_line, c.content, c.token_count, c.embedding
            from chunks c join files f on f.id = c.file_id
            where c.analysis_id = $1
            """, conn))
        {
            cmd.Parameters.AddWithValue(analysisId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                chunks.Add(new BundleChunk(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetInt32(4),
                    EmbeddingCodec.Encode(reader.IsDBNull(5) ? null : reader.GetFieldValue<Vector>(5).ToArray())));
            }
        }

        var components = new List<BundleComponent>();
        await using (var cmd = new NpgsqlCommand(
            "select name, root_paths, file_count, plan_rank from components where analysis_id = $1 order by plan_rank",
            conn))
        {
            cmd.Parameters.AddWithValue(analysisId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                components.Add(new BundleComponent(
                    reader.GetString(0), [.. reader.GetFieldValue<string[]>(1)],
                    reader.GetInt32(2), reader.GetInt32(3)));
            }
        }

        var summaries = new List<BundleSummary>();
        await using (var cmd = new NpgsqlCommand(
            """
            select s.scope, c.name, s.prose_md, s.structured::text, s.model, s.prompt_version, s.embedding
            from summaries s left join components c on c.id = s.component_id
            where s.analysis_id = $1
            """, conn))
        {
            cmd.Parameters.AddWithValue(analysisId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                summaries.Add(new BundleSummary(
                    reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetString(5),
                    EmbeddingCodec.Encode(reader.IsDBNull(6) ? null : reader.GetFieldValue<Vector>(6).ToArray())));
            }
        }

        var experience = await BuildExperienceAsync(conn, sessionId, ct);

        return new BundleDocument(
            BundleDocument.CurrentVersion, owner, name, url, kind, prNumber, commitSha,
            experience.Model, analysisStatus, planJson, metaJson,
            files, chunks, components, summaries, experience);
    }

    private static async Task<BundleExperience> BuildExperienceAsync(
        NpgsqlConnection conn, Guid sessionId, CancellationToken ct)
    {
        Guid experienceId;
        string commitSha, model;
        await using (var cmd = new NpgsqlCommand(
            "select id, commit_sha, model from experiences where session_id = $1 order by version desc limit 1", conn))
        {
            cmd.Parameters.AddWithValue(sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException("session has no experience to export");
            }

            experienceId = reader.GetGuid(0);
            commitSha = reader.GetString(1);
            model = reader.GetString(2);
        }

        var sections = new List<BundleSection>();
        var sectionIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(
            """
            select id, slug, kind, title, summary, ord, depth, estimated_minutes, embedding
            from sections where experience_id = $1 and status = 'ready' order by ord
            """, conn))
        {
            cmd.Parameters.AddWithValue(experienceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sectionIds.Add(reader.GetGuid(0));
                sections.Add(new BundleSection(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                    EmbeddingCodec.Encode(reader.IsDBNull(8) ? null : reader.GetFieldValue<Vector>(8).ToArray()),
                    [], null));
            }
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var blocks = new List<BundleBlock>();
            await using (var cmd = new NpgsqlCommand(
                "select type, data::text from blocks where section_id = $1 order by ord", conn))
            {
                cmd.Parameters.AddWithValue(sectionIds[i]);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    blocks.Add(new BundleBlock(reader.GetString(0), reader.GetString(1)));
                }
            }

            BundleQuiz? quiz = null;
            await using (var cmd = new NpgsqlCommand(
                """
                select q.title, qq.type, qq.prompt, qq.data::text
                from quizzes q join quiz_questions qq on qq.quiz_id = q.id
                where q.section_id = $1 order by qq.ord
                """, conn))
            {
                cmd.Parameters.AddWithValue(sectionIds[i]);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var questions = new List<BundleQuizQuestion>();
                string? title = null;
                while (await reader.ReadAsync(ct))
                {
                    title ??= reader.GetString(0);
                    questions.Add(new BundleQuizQuestion(
                        reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                }

                if (title is not null)
                {
                    quiz = new BundleQuiz(title, questions);
                }
            }

            sections[i] = sections[i] with { Blocks = blocks, Quiz = quiz };
        }

        return new BundleExperience(commitSha, model, sections);
    }
}
