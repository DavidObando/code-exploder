using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Llm;

/// <summary>
/// OpenAI-compatible chat client (works against Ollama's /v1). Temperature is pinned
/// to 0 for determinism (docs/06-llm-strategy.md); the starved-output guard turns
/// "budget burned, no content" into a retryable <see cref="LlmStarvedException"/>.
/// </summary>
public sealed class LlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmClient> _logger;
    private readonly Uri _chatEndpoint;

    public LlmClient(HttpClient http, LlmOptions options, ILogger<LlmClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _options = options;
        _logger = logger;
        _chatEndpoint = new Uri(NormalizeBaseUrl(options.BaseUrl) + "/chat/completions");
    }

    /// <summary>Trims trailing slashes and appends "/v1" when the configured root lacks it.</summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var normalized = baseUrl.TrimEnd('/');
        if (!normalized.EndsWith("/v1", StringComparison.Ordinal))
        {
            normalized += "/v1";
        }

        return normalized;
    }

    public async Task<LlmResult> ChatAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        using var message = BuildRequest(request, stream: false);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        ThrowIfNotSuccess(response, body);

        string? content = null;
        var finishReason = string.Empty;
        LlmUsage? usage = null;
        using (var doc = JsonDocument.Parse(body))
        {
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new LlmException("LLM response contained no choices: " + Excerpt(body));
            }

            var choice = choices[0];
            if (choice.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString();
            }

            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString() ?? string.Empty;
            }

            if (doc.RootElement.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = new LlmUsage(
                    usageElement.TryGetProperty("prompt_tokens", out var prompt) && prompt.ValueKind == JsonValueKind.Number ? prompt.GetInt32() : 0,
                    usageElement.TryGetProperty("completion_tokens", out var completion) && completion.ValueKind == JsonValueKind.Number ? completion.GetInt32() : 0);
            }
        }

        if (finishReason == "length" && string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("Model {Model} hit the output budget with no content (starved output)", _options.Model);
            throw new LlmStarvedException(
                $"Model '{_options.Model}' consumed the output token budget without producing content (finish_reason=length).");
        }

        _logger.LogDebug(
            "Chat completed: finish={FinishReason} promptTokens={PromptTokens} completionTokens={CompletionTokens}",
            finishReason,
            usage?.PromptTokens,
            usage?.CompletionTokens);
        return new LlmResult(content ?? string.Empty, finishReason, usage);
    }

    public async IAsyncEnumerable<string> ChatStreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        using var message = BuildRequest(request, stream: true);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            ThrowIfNotSuccess(response, body);
        }

        var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                yield break;
            }

            var delta = ExtractDelta(payload);
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    private static string? ExtractDelta(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null; // e.g. the final usage-only chunk
        }

        if (choices[0].TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        return null;
    }

    private HttpRequestMessage BuildRequest(LlmRequest request, bool stream)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            ["temperature"] = 0,
            ["max_tokens"] = request.MaxOutputTokens ?? _options.MaxOutputTokens,
            ["stream"] = stream,
        };
        if (request.Json && _options.JsonMode)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private static void ThrowIfNotSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new LlmException($"LLM request failed with HTTP {(int)response.StatusCode}: {Excerpt(body)}");
    }

    private static string Excerpt(string body) => body.Length > 400 ? body[..400] : body;
}
