namespace CodeExploder.Storage.Bundles;

/// <summary>
/// The portable form of a completed analysis (docs/08 §M7): everything a ready
/// session needs — knowledge base included — and nothing user-specific (no progress,
/// attempts, threads, jobs, or events). Serialized as gzipped JSON (*.cxbundle.gz);
/// embeddings ride as base64-encoded little-endian float32 so they restore exactly.
/// The format doubles as the backup/restore unit for analyses.
/// </summary>
public sealed record BundleDocument(
    int Version,
    string RepoOwner,
    string RepoName,
    string RepoUrl,
    string Kind,
    int? PrNumber,
    string CommitSha,
    string Model,
    string AnalysisStatus,
    string? PlanJson,
    string? MetaJson,
    List<BundleFile> Files,
    List<BundleChunk> Chunks,
    List<BundleComponent> Components,
    List<BundleSummary> Summaries,
    BundleExperience Experience)
{
    public const int CurrentVersion = 1;
}

public sealed record BundleFile(
    string Path, string Language, long SizeBytes, bool Excluded, string? ExcludeReason,
    int Role, int Churn, int Rank);

public sealed record BundleChunk(
    string FilePath, int StartLine, int EndLine, string Content, int TokenCount, string? EmbeddingB64);

public sealed record BundleComponent(string Name, List<string> RootPaths, int FileCount, int PlanRank);

public sealed record BundleSummary(
    string Scope, string? ComponentName, string ProseMd, string? StructuredJson,
    string Model, string PromptVersion, string? EmbeddingB64);

public sealed record BundleExperience(string CommitSha, string Model, List<BundleSection> Sections);

public sealed record BundleSection(
    string Slug, string Kind, string Title, string Summary, int Ord, int Depth,
    int EstimatedMinutes, string? EmbeddingB64, List<BundleBlock> Blocks, BundleQuiz? Quiz);

public sealed record BundleBlock(string Type, string DataJson);

public sealed record BundleQuiz(string Title, List<BundleQuizQuestion> Questions);

public sealed record BundleQuizQuestion(string Type, string Prompt, string DataJson);

public static class EmbeddingCodec
{
    public static string? Encode(float[]? embedding)
    {
        if (embedding is null)
        {
            return null;
        }

        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[]? Decode(string? b64)
    {
        if (b64 is null)
        {
            return null;
        }

        var bytes = Convert.FromBase64String(b64);
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
