using System.Text.RegularExpressions;
using CodeExploder.Domain;
using Npgsql;
using Pgvector;

namespace CodeExploder.Qa;

public sealed record RetrievedChunk(Guid ChunkId, string Path, int StartLine, int EndLine, string Content);

public sealed record RetrievedProse(string Kind, string Title, string Text);

public sealed record RetrievalResult(IReadOnlyList<RetrievedProse> Prose, IReadOnlyList<RetrievedChunk> Chunks);

/// <summary>
/// Retrieval fusion (docs/06 §retrieval): three scoped ANN searches plus an FTS and a
/// trigram lexical leg (code identifiers embed poorly), fused with reciprocal-rank
/// fusion. Everything is scoped to the session's analysis.
/// </summary>
public sealed partial class Retriever(NpgsqlDataSource dataSource)
{
    private const int RrfK = 60;

    public async Task<RetrievalResult> RetrieveAsync(
        Guid analysisId, Guid sessionId, float[] questionEmbedding, string questionText,
        CancellationToken ct = default)
    {
        var vector = new Vector(questionEmbedding);

        var codeRanks = await VectorChunksAsync(analysisId, vector, docs: false, limit: 12, ct);
        var docRanks = await VectorChunksAsync(analysisId, vector, docs: true, limit: 6, ct);
        var ftsRanks = await FtsChunksAsync(analysisId, questionText, limit: 8, ct);
        var trgmRanks = await TrigramChunksAsync(analysisId, questionText, limit: 5, ct);

        // RRF across the four chunk lists; docs keep a seat via their own list.
        var scores = new Dictionary<Guid, double>();
        var byId = new Dictionary<Guid, RetrievedChunk>();
        foreach (var list in new[] { codeRanks, docRanks, ftsRanks, trgmRanks })
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var chunk = list[rank];
                scores[chunk.ChunkId] = scores.GetValueOrDefault(chunk.ChunkId) + 1.0 / (RrfK + rank + 1);
                byId[chunk.ChunkId] = chunk;
            }
        }

        var fused = scores.OrderByDescending(kv => kv.Value)
            .Take(12)
            .Select(kv => byId[kv.Key])
            .ToList();

        var prose = await ProseAsync(analysisId, sessionId, vector, ct);
        return new RetrievalResult(prose, fused);
    }

    private async Task<List<RetrievedChunk>> VectorChunksAsync(
        Guid analysisId, Vector vector, bool docs, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            select c.id, f.path, c.start_line, c.end_line, c.content
            from chunks c
            join files f on f.id = c.file_id
            where c.analysis_id = $1 and c.embedding is not null
              and (f.role & {(int)FileRole.Doc}) {(docs ? "<>" : "=")} 0
            order by c.embedding <=> $2
            limit {limit}
            """);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(vector);
        return await ReadChunksAsync(cmd, ct);
    }

    private async Task<List<RetrievedChunk>> FtsChunksAsync(
        Guid analysisId, string question, int limit, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            select c.id, f.path, c.start_line, c.end_line, c.content
            from chunks c
            join files f on f.id = c.file_id, plainto_tsquery('simple', $2) q
            where c.analysis_id = $1 and c.tsv @@ q
            order by ts_rank(c.tsv, q) desc
            limit {limit}
            """);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(question);
        return await ReadChunksAsync(cmd, ct);
    }

    private async Task<List<RetrievedChunk>> TrigramChunksAsync(
        Guid analysisId, string question, int limit, CancellationToken ct)
    {
        // Identifier-shaped tokens (CamelCase, snake_case, dotted) — take the longest.
        var token = IdentifierPattern().Matches(question)
            .Select(m => m.Value)
            .Where(t => t.Length >= 5 && (t.Any(char.IsUpper) || t.Contains('_') || t.Contains('.')))
            .OrderByDescending(t => t.Length)
            .FirstOrDefault();
        if (token is null)
        {
            return [];
        }

        await using var cmd = dataSource.CreateCommand(
            $"""
            select c.id, f.path, c.start_line, c.end_line, c.content
            from chunks c
            join files f on f.id = c.file_id
            where c.analysis_id = $1 and c.content ilike $2
            order by c.start_line
            limit {limit}
            """);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue($"%{token}%");
        return await ReadChunksAsync(cmd, ct);
    }

    private async Task<List<RetrievedProse>> ProseAsync(
        Guid analysisId, Guid sessionId, Vector vector, CancellationToken ct)
    {
        var prose = new List<RetrievedProse>();
        await using (var cmd = dataSource.CreateCommand(
            """
            select coalesce(c.name, 'repository overview'), s.prose_md
            from summaries s
            left join components c on c.id = s.component_id
            where s.analysis_id = $1 and s.embedding is not null
            order by s.embedding <=> $2
            limit 4
            """))
        {
            cmd.Parameters.AddWithValue(analysisId);
            cmd.Parameters.AddWithValue(vector);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                prose.Add(new RetrievedProse("summary", reader.GetString(0), reader.GetString(1)));
            }
        }

        await using (var cmd = dataSource.CreateCommand(
            """
            select sec.title, string_agg(b.data->>'md', E'\n\n' order by b.ord)
            from sections sec
            join experiences e on e.id = sec.experience_id
            join blocks b on b.section_id = sec.id and b.type in ('markdown','callout')
            where e.session_id = $1 and sec.embedding is not null
              and sec.id in (
                  select sec2.id from sections sec2
                  join experiences e2 on e2.id = sec2.experience_id
                  where e2.session_id = $1 and sec2.embedding is not null
                  order by sec2.embedding <=> $2 limit 2)
            group by sec.id, sec.title
            """))
        {
            cmd.Parameters.AddWithValue(sessionId);
            cmd.Parameters.AddWithValue(vector);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                prose.Add(new RetrievedProse("section", reader.GetString(0), reader.GetString(1)));
            }
        }

        return prose;
    }

    private static async Task<List<RetrievedChunk>> ReadChunksAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var chunks = new List<RetrievedChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            chunks.Add(new RetrievedChunk(
                reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4)));
        }

        return chunks;
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_.]{2,}", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex IdentifierPattern();
}
