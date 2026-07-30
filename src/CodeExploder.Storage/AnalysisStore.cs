using CodeExploder.Domain;
using Npgsql;

namespace CodeExploder.Storage;

/// <summary>
/// Raw-SQL store for the analysis-side tables (files, chunks, components, plan).
/// Bulk paths use binary COPY — a mid-size repo inserts thousands of chunk rows.
/// </summary>
public sealed class AnalysisStore(NpgsqlDataSource dataSource)
{
    public async Task SetCommitShaAsync(Guid analysisId, string sha, string? metaJson, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update analyses set commit_sha = $2, meta = $3::jsonb where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(sha);
        cmd.Parameters.AddWithValue((object?)metaJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replaces the analysis's file rows; returns path → id for chunk FKs.</summary>
    public async Task<IReadOnlyDictionary<string, Guid>> InsertFilesAsync(
        Guid analysisId, IReadOnlyList<RepoFile> files, CancellationToken ct = default)
    {
        var ids = new Dictionary<string, Guid>(files.Count, StringComparer.Ordinal);
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var clear = new NpgsqlCommand("delete from files where analysis_id = $1", conn))
        {
            clear.Parameters.AddWithValue(analysisId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        await using var import = await conn.BeginBinaryImportAsync(
            """
            copy files (id, analysis_id, path, language, size_bytes, excluded, exclude_reason, role, churn, rank)
            from stdin (format binary)
            """, ct);
        foreach (var file in files)
        {
            var id = Guid.NewGuid();
            ids[file.Path] = id;
            await import.StartRowAsync(ct);
            await import.WriteAsync(id, ct);
            await import.WriteAsync(analysisId, ct);
            await import.WriteAsync(file.Path, ct);
            await import.WriteAsync(file.Language, ct);
            await import.WriteAsync(file.SizeBytes, ct);
            await import.WriteAsync(file.Excluded, ct);
            if (file.ExcludeReason is null)
            {
                await import.WriteNullAsync(ct);
            }
            else
            {
                await import.WriteAsync(file.ExcludeReason, ct);
            }

            await import.WriteAsync((int)file.Role, ct);
            await import.WriteAsync(file.Churn, ct);
            await import.WriteAsync(file.Rank, ct);
        }

        await import.CompleteAsync(ct);
        return ids;
    }

    public async Task<int> InsertChunksAsync(
        Guid analysisId,
        IReadOnlyDictionary<string, Guid> fileIds,
        IEnumerable<CodeChunk> chunks,
        CancellationToken ct = default)
    {
        var count = 0;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var import = await conn.BeginBinaryImportAsync(
            """
            copy chunks (id, analysis_id, file_id, start_line, end_line, content, token_count)
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
            count++;
        }

        await import.CompleteAsync(ct);
        return count;
    }

    public async Task InsertComponentsAsync(
        Guid analysisId, IReadOnlyList<Component> components, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using (var clear = new NpgsqlCommand("delete from components where analysis_id = $1", conn))
        {
            clear.Parameters.AddWithValue(analysisId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        var rank = 0;
        foreach (var component in components)
        {
            await using var cmd = new NpgsqlCommand(
                """
                insert into components (id, analysis_id, name, root_paths, file_count, plan_rank)
                values ($1, $2, $3, $4, $5, $6)
                """, conn);
            cmd.Parameters.AddWithValue(Guid.NewGuid());
            cmd.Parameters.AddWithValue(analysisId);
            cmd.Parameters.AddWithValue(component.Name);
            cmd.Parameters.AddWithValue(component.RootPaths.ToArray());
            cmd.Parameters.AddWithValue(component.FilePaths.Count);
            cmd.Parameters.AddWithValue(rank++);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task SavePlanAsync(Guid analysisId, string planJson, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("update analyses set plan = $2::jsonb where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(planJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetPlanAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("select plan::text from analyses where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<string?> GetCommitShaAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("select commit_sha from analyses where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<string?> GetMetaAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("select meta::text from analyses where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<(Guid Id, string Name)>> GetComponentsAsync(
        Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, name from components where analysis_id = $1 order by plan_rank");
        cmd.Parameters.AddWithValue(analysisId);
        var rows = new List<(Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    public async Task InsertSummaryAsync(
        Guid analysisId, string scope, Guid? componentId, string proseMd, string? structuredJson,
        string model, string promptVersion, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            insert into summaries (id, analysis_id, scope, component_id, prose_md, structured, model, prompt_version)
            values ($1, $2, $3, $4, $5, $6::jsonb, $7, $8)
            """);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(scope);
        cmd.Parameters.AddWithValue((object?)componentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(proseMd);
        cmd.Parameters.AddWithValue((object?)structuredJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(promptVersion);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Component summaries as (componentName, proseMd, structuredJson) for the S5 pack.</summary>
    public async Task<IReadOnlyList<(string Component, string ProseMd, string? StructuredJson)>> GetComponentSummariesAsync(
        Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select c.name, s.prose_md, s.structured::text
            from summaries s
            join components c on c.id = s.component_id
            where s.analysis_id = $1 and s.scope = 'component'
            order by c.plan_rank
            """);
        cmd.Parameters.AddWithValue(analysisId);
        var rows = new List<(string, string, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    public async Task<string?> GetRepoSummaryStructuredAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select structured::text from summaries where analysis_id = $1 and scope = 'repo' order by created_at desc limit 1");
        cmd.Parameters.AddWithValue(analysisId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<int> CountComponentSummariesAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select count(*) from summaries where analysis_id = $1 and scope = 'component'");
        cmd.Parameters.AddWithValue(analysisId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task UpdateChunkEmbeddingsAsync(
        IReadOnlyList<(Guid ChunkId, float[] Embedding)> embeddings, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var (chunkId, embedding) in embeddings)
        {
            await using var cmd = new NpgsqlCommand("update chunks set embedding = $2 where id = $1", conn);
            cmd.Parameters.AddWithValue(chunkId);
            cmd.Parameters.AddWithValue(new Pgvector.Vector(embedding));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<(Guid Id, string Content)>> GetUnembeddedChunksAsync(
        Guid analysisId, int limit, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, content from chunks where analysis_id = $1 and embedding is null order by id limit $2");
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(limit);
        var rows = new List<(Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    public async Task SetSummaryEmbeddingAsync(Guid summaryId, float[] embedding, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("update summaries set embedding = $2 where id = $1");
        cmd.Parameters.AddWithValue(summaryId);
        cmd.Parameters.AddWithValue(new Pgvector.Vector(embedding));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(Guid Id, string Text)>> GetUnembeddedSummariesAsync(
        Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select id, prose_md from summaries where analysis_id = $1 and embedding is null");
        cmd.Parameters.AddWithValue(analysisId);
        var rows = new List<(Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return rows;
    }

    public async Task<(int Embedded, int Total)> EmbeddingCoverageAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select count(*) filter (where embedding is not null), count(*) from chunks where analysis_id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    public async Task<int> CountChunksAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("select count(*) from chunks where analysis_id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
