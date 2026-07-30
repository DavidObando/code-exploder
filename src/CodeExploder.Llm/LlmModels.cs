namespace CodeExploder.Llm;

public sealed record LlmMessage(string Role, string Content);

/// <summary>A single chat call. MaxOutputTokens null falls back to <see cref="LlmOptions.MaxOutputTokens"/>.</summary>
public sealed record LlmRequest(IReadOnlyList<LlmMessage> Messages, int? MaxOutputTokens = null, bool Json = false);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens);

public sealed record LlmResult(string Content, string FinishReason, LlmUsage? Usage);

public interface ILlmClient
{
    Task<LlmResult> ChatAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>Streams content deltas as they arrive; completes at the server's [DONE] marker.</summary>
    IAsyncEnumerable<string> ChatStreamAsync(LlmRequest request, CancellationToken ct = default);
}
