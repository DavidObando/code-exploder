using Microsoft.Extensions.Logging;

namespace CodeExploder.Llm;

/// <summary>
/// Probes the OpenAI-compatible endpoint before dispatching LLM work; runs park
/// (rather than fail) when the model host is unavailable (docs/06-llm-strategy.md).
/// </summary>
public sealed class LlmReadinessGate
{
    private const int MaxProbeSeconds = 5;

    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly ILogger<LlmReadinessGate> _logger;
    private readonly Uri _modelsEndpoint;

    public LlmReadinessGate(HttpClient http, LlmOptions options, ILogger<LlmReadinessGate> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _options = options;
        _logger = logger;
        _modelsEndpoint = new Uri(LlmClient.NormalizeBaseUrl(options.BaseUrl) + "/models");
    }

    /// <summary>GET {BaseUrl}/models with a short timeout; false on any failure.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(MaxProbeSeconds, _options.TimeoutSeconds)));
            using var response = await _http.GetAsync(_modelsEndpoint, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "LLM readiness probe failed for {Endpoint}", _modelsEndpoint);
            return false;
        }
    }
}
