namespace CodeExploder.Llm;

/// <summary>
/// Configuration for the OpenAI-compatible chat endpoint (Ollama's /v1 in the home
/// deployment). Bound from the "Llm" config section by <see cref="LlmServices"/>.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>Endpoint root; "/v1" is appended when missing. Never a LAN IP — public repo.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";

    public string Model { get; set; } = "qwen3-coder:oc";

    public int MaxOutputTokens { get; set; } = 4096;

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>When true, requests that ask for JSON get response_format json_object.</summary>
    public bool JsonMode { get; set; } = true;
}
