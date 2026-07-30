using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodeExploder.GitHub;

public sealed record GitHubRepoInfo(
    string? Description,
    string DefaultBranch,
    long SizeKb,
    string? License,
    IReadOnlyList<string> Topics);

/// <summary>
/// Best-effort anonymous GitHub REST enrichment (docs/01-analysis-pipeline.md §S0):
/// ~1 call per analysis against the 60/h anonymous limit, used for the description,
/// size pre-check, and default branch. Any failure — rate limit, network, 404 —
/// degrades to null and the pipeline proceeds from the clone alone. An optional
/// GITHUB_TOKEN env var raises the rate-limit headroom.
/// </summary>
public sealed class GitHubApiClient(HttpClient http, ILogger<GitHubApiClient> logger)
{
    public async Task<GitHubRepoInfo?> GetRepoAsync(string owner, string name, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri($"repos/{owner}/{name}", UriKind.Relative), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("GitHub API repo lookup returned {Status} for {Owner}/{Name}",
                    (int)response.StatusCode, owner, name);
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return new GitHubRepoInfo(
                json.TryGetProperty("description", out var d) ? d.GetString() : null,
                json.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "main" : "main",
                json.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                json.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.Object
                    && l.TryGetProperty("spdx_id", out var spdx) ? spdx.GetString() : null,
                json.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToList()
                    : []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(ex, "GitHub API repo lookup failed for {Owner}/{Name}; proceeding without it", owner, name);
            return null;
        }
    }

    public sealed record GitHubPrInfo(string? Title, string? Body, string BaseRef);

    /// <summary>Best-effort PR metadata for the narrative stages; null degrades gracefully.</summary>
    public async Task<GitHubPrInfo?> GetPullRequestAsync(
        string owner, string name, int number, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                new Uri($"repos/{owner}/{name}/pulls/{number}", UriKind.Relative), ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return new GitHubPrInfo(
                json.TryGetProperty("title", out var t) ? t.GetString() : null,
                json.TryGetProperty("body", out var b) ? b.GetString() : null,
                json.TryGetProperty("base", out var bs) && bs.TryGetProperty("ref", out var r)
                    ? r.GetString() ?? "main"
                    : "main");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(ex, "GitHub PR lookup failed for {Owner}/{Name}#{Number}", owner, name, number);
            return null;
        }
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("code-exploder/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }
}
