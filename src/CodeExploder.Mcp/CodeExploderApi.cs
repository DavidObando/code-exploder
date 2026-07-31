using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CodeExploder.Mcp;

/// <summary>
/// Thin HTTP client over the Gateway API (docs/08 §M8 — the KB has exactly one
/// contract). Point CX_BASE_URL at a local gateway (DevBypass) or the deployed
/// hostname with CX_BASIC_AUTH="user:pass" to ride the edge auth gate remotely.
/// </summary>
public sealed class CodeExploderApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public void Dispose() => _http.Dispose();

    public CodeExploderApi(string baseUrl, string? basicAuth)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(180),
        };
        if (!string.IsNullOrWhiteSpace(basicAuth))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basicAuth)));
        }
    }

    public async Task<string> ListSessionsAsync(CancellationToken ct)
    {
        var sessions = await GetAsync("api/sessions", ct);
        var sb = new StringBuilder("Sessions (id | kind | status | title):\n");
        foreach (var s in sessions.EnumerateArray())
        {
            sb.AppendLine($"- {s.GetProperty("id").GetString()} | {s.GetProperty("kind").GetString()}"
                + $" | {s.GetProperty("status").GetString()} | {s.GetProperty("title").GetString()}");
        }

        return sb.ToString();
    }

    public async Task<string> GetRepoSummaryAsync(string sessionId, CancellationToken ct)
    {
        var snapshot = await GetAsync($"api/sessions/{sessionId}/analysis", ct);
        if (!snapshot.TryGetProperty("summary", out var summary) || summary.ValueKind == JsonValueKind.Null)
        {
            return "No repository summary yet (analysis not finished).";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {summary.GetProperty("description").GetString() ?? "(no description)"}");
        sb.AppendLine($"Commit: {summary.GetProperty("commitSha").GetString()}");
        sb.AppendLine($"Files: {summary.GetProperty("analyzedFileCount").GetInt32()} analyzed, "
            + $"{summary.GetProperty("excludedFileCount").GetInt32()} excluded; "
            + $"chunks: {summary.GetProperty("chunkCount").GetInt32()}");
        sb.AppendLine("Languages: " + string.Join(", ", summary.GetProperty("languages").EnumerateArray()
            .Select(l => $"{l.GetProperty("name").GetString()} {l.GetProperty("percent").GetDouble():0}%")));
        sb.AppendLine("Build systems: " + string.Join(", ", summary.GetProperty("buildSystems").EnumerateArray()
            .Select(b => b.GetString())));
        sb.AppendLine("Components: " + string.Join(", ", summary.GetProperty("components").EnumerateArray()
            .Select(c => $"{c.GetProperty("name").GetString()} ({c.GetProperty("fileCount").GetInt32()})")));
        return sb.ToString();
    }

    public async Task<string> ListSectionsAsync(string sessionId, CancellationToken ct)
    {
        var toc = await GetAsync($"api/sessions/{sessionId}/experience", ct);
        var sb = new StringBuilder("Sections (slug | kind | status | title):\n");
        foreach (var s in toc.GetProperty("sections").EnumerateArray())
        {
            sb.AppendLine($"- {s.GetProperty("slug").GetString()} | {s.GetProperty("kind").GetString()}"
                + $" | {s.GetProperty("status").GetString()} | {s.GetProperty("title").GetString()}");
        }

        return sb.ToString();
    }

    public async Task<string> GetSectionAsync(string sessionId, string slug, CancellationToken ct)
    {
        var toc = await GetAsync($"api/sessions/{sessionId}/experience", ct);
        var entry = toc.GetProperty("sections").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("slug").GetString() == slug);
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return $"No section with slug '{slug}'. Use list_sections first.";
        }

        var section = await GetAsync($"api/sections/{entry.GetProperty("id").GetString()}", ct);
        var sb = new StringBuilder($"# {section.GetProperty("title").GetString()}\n\n");
        foreach (var block in section.GetProperty("blocks").EnumerateArray())
        {
            var data = block.GetProperty("data");
            switch (block.GetProperty("type").GetString())
            {
                case "markdown":
                    sb.AppendLine(data.GetProperty("md").GetString()).AppendLine();
                    break;
                case "code":
                    sb.AppendLine($"`{data.GetProperty("path").GetString()}:"
                        + $"{data.GetProperty("startLine").GetInt32()}-{data.GetProperty("endLine").GetInt32()}`");
                    sb.AppendLine("```" + (data.GetProperty("language").GetString() ?? ""));
                    sb.AppendLine(data.GetProperty("content").GetString());
                    sb.AppendLine("```").AppendLine();
                    break;
                case "callout":
                    sb.AppendLine($"> **{data.GetProperty("title").GetString()}**: {data.GetProperty("md").GetString()}")
                      .AppendLine();
                    break;
                case "diagram":
                    sb.AppendLine($"```mermaid\n{data.GetProperty("mermaid").GetString()}\n```").AppendLine();
                    foreach (var stage in data.GetProperty("stages").EnumerateArray())
                    {
                        sb.AppendLine($"*{stage.GetProperty("title").GetString()}*: "
                            + stage.GetProperty("narrationMd").GetString());
                    }

                    sb.AppendLine();
                    break;
                default:
                    break;
            }
        }

        return sb.ToString();
    }

    public async Task<string> SearchAsync(string sessionId, string query, int k, CancellationToken ct)
    {
        var result = await GetAsync(
            $"api/sessions/{sessionId}/search?q={Uri.EscapeDataString(query)}&k={k}", ct);
        var sb = new StringBuilder();
        foreach (var prose in result.GetProperty("prose").EnumerateArray())
        {
            sb.AppendLine($"## Background ({prose.GetProperty("kind").GetString()}): {prose.GetProperty("title").GetString()}");
            sb.AppendLine(prose.GetProperty("text").GetString()).AppendLine();
        }

        foreach (var hit in result.GetProperty("chunks").EnumerateArray())
        {
            sb.AppendLine($"## {hit.GetProperty("path").GetString()}:"
                + $"{hit.GetProperty("startLine").GetInt32()}-{hit.GetProperty("endLine").GetInt32()}");
            sb.AppendLine("```");
            sb.AppendLine(hit.GetProperty("snippet").GetString());
            sb.AppendLine("```").AppendLine();
        }

        return sb.Length > 0 ? sb.ToString() : "No results.";
    }

    public async Task<string> AskExpertAsync(string sessionId, string question, CancellationToken ct)
    {
        // Find-or-create the adapter's thread, then poll the streaming answer to
        // completion — MCP tool calls are request/response, not streams.
        var threads = await GetAsync($"api/sessions/{sessionId}/threads", ct);
        var threadId = threads.EnumerateArray()
            .FirstOrDefault(t => t.GetProperty("title").GetString() == "MCP")
            is { ValueKind: JsonValueKind.Object } existing
            ? existing.GetProperty("id").GetString()!
            : (await PostAsync($"api/sessions/{sessionId}/threads", new { title = "MCP" }, ct))
                .GetProperty("id").GetString()!;

        var send = await PostAsync($"api/threads/{threadId}/messages", new { content = question }, ct);
        var messageId = send.GetProperty("assistantMessageId").GetString();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(150);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var messages = await GetAsync($"api/threads/{threadId}/messages", ct);
            var answer = messages.EnumerateArray()
                .FirstOrDefault(m => m.GetProperty("id").GetString() == messageId);
            if (answer.ValueKind == JsonValueKind.Object
                && answer.GetProperty("status").GetString() is "complete" or "error" or "cancelled")
            {
                var sb = new StringBuilder(answer.GetProperty("content").GetString());
                if (answer.TryGetProperty("citations", out var citations)
                    && citations.ValueKind == JsonValueKind.Array)
                {
                    sb.AppendLine("\n\nSources:");
                    foreach (var c in citations.EnumerateArray())
                    {
                        sb.AppendLine($"- [{c.GetProperty("label").GetString()}] {c.GetProperty("path").GetString()}:"
                            + $"{c.GetProperty("startLine").GetInt32()}-{c.GetProperty("endLine").GetInt32()}");
                    }
                }

                return sb.ToString();
            }
        }

        return "The expert is taking too long — try again (the answer may still complete in the app).";
    }

    private async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(url, UriKind.Relative), ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
    }

    private async Task<JsonElement> PostAsync(string url, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, JsonOpts, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gateway returned {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
    }
}
