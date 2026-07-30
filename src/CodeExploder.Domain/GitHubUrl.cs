using System.Text.RegularExpressions;

namespace CodeExploder.Domain;

/// <summary>
/// Parses the single URL input the new-session form accepts: a public GitHub repo
/// (https://github.com/{owner}/{repo}) or pull request (…/pull/{n}). Anything else is
/// rejected — v1 supports public GitHub only (docs/00-overview.md).
/// </summary>
public sealed partial record GitHubUrl(string Owner, string Name, int? PrNumber)
{
    public string Kind => PrNumber is null ? SessionKind.Repo : SessionKind.Pr;

    public string CanonicalUrl => $"https://github.com/{Owner}/{Name}";

    public string Title => PrNumber is { } pr ? $"{Owner}/{Name} PR #{pr}" : $"{Owner}/{Name}";

    public static bool TryParse(string? url, out GitHubUrl parsed)
    {
        parsed = new GitHubUrl(string.Empty, string.Empty, null);
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var match = Pattern().Match(url.Trim());
        if (!match.Success)
        {
            return false;
        }

        int? prNumber = match.Groups["pr"].Success ? int.Parse(match.Groups["pr"].Value) : null;
        parsed = new GitHubUrl(match.Groups["owner"].Value, match.Groups["name"].Value, prNumber);
        return true;
    }

    [GeneratedRegex(
        @"^https://(www\.)?github\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)/(?<name>[A-Za-z0-9._-]+?)(\.git)?(/(pull/(?<pr>\d+))?)?/?$",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
