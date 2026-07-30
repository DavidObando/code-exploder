using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Llm;

public interface IEmbedClient
{
    /// <summary>Embeds a batch of texts; result[i] corresponds to inputs[i].</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
}

/// <summary>
/// Embeddings via the OpenAI-compatible /v1/embeddings endpoint (Ollama serves it for
/// nomic-embed-text). Shares the LLM base URL — the embedder co-resides with the
/// generation model (docs/06 §measured co-residency).
/// </summary>
public sealed class OllamaEmbedClient(HttpClient http, LlmOptions options, ILogger<OllamaEmbedClient> logger) : IEmbedClient
{
    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var url = LlmClient.NormalizeBaseUrl(options.BaseUrl) + "/embeddings";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var response = await http.PostAsJsonAsync(
            new Uri(url), new { model = options.EmbedModel, input = inputs }, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            logger.LogWarning("Embedding request failed ({Status}): {Body}",
                (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
            throw new LlmException($"embeddings request failed with {(int)response.StatusCode}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        var data = json.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.GetProperty("index").GetInt32())
            .Select(d => d.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray())
            .ToList();
        if (data.Count != inputs.Count)
        {
            throw new LlmException($"embeddings returned {data.Count} vectors for {inputs.Count} inputs");
        }

        return data;
    }
}
