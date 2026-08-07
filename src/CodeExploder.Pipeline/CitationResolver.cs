using System.Text.Json;
using System.Text.RegularExpressions;
using CodeExploder.Domain;
using Microsoft.Extensions.Logging;

namespace CodeExploder.Pipeline;

/// <summary>
/// Splits section markdown at {{cite:path:start-end}} tokens into alternating
/// markdown/code blocks. Code content is snapshotted from the workspace at generation
/// time (renders without GitHub, SHA-pinned deep links come from the client). Tokens
/// whose path isn't in the analyzed file set are dropped and logged — the
/// anti-hallucination check of docs/01 §1.7.
/// </summary>
public static partial class CitationResolver
{
    // Tight enough to read as a surgical excerpt (a method or small type), not a
    // page. The model tends to cite from line 1 because it only sees file heads;
    // the preamble skip below keeps that from becoming a wall of usings.
    private const int MaxCitedLines = 36;

    public static IReadOnlyList<(string Type, string DataJson)> Resolve(
        string markdown, string workspaceRoot, RepoMap map, ILogger logger)
    {
        var files = map.Files.Where(f => !f.Excluded).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var blocks = new List<(string, string)>();
        var cursor = 0;

        foreach (Match match in CitePattern().Matches(markdown))
        {
            var before = markdown[cursor..match.Index].Trim();
            if (before.Length > 0)
            {
                blocks.Add((BlockType.Markdown, JsonSerializer.Serialize(new { md = before }, Json.Opts)));
            }

            cursor = match.Index + match.Length;

            var path = match.Groups[1].Value.Trim();
            if (!files.TryGetValue(path, out var file))
            {
                logger.LogInformation("Dropped citation to unknown file: {Path}", path);
                continue;
            }

            var start = int.Parse(match.Groups[2].Value);
            var end = int.Parse(match.Groups[3].Value);
            if (ReadRange(workspaceRoot, path, ref start, ref end) is not { } content)
            {
                logger.LogInformation("Dropped unreadable citation: {Path}:{Start}-{End}", path, start, end);
                continue;
            }

            blocks.Add((BlockType.Code, JsonSerializer.Serialize(new
            {
                path,
                startLine = start,
                endLine = end,
                language = file.Language,
                content,
                captionMd = (string?)null,
            }, Json.Opts)));
        }

        var rest = markdown[cursor..].Trim();
        if (rest.Length > 0)
        {
            blocks.Add((BlockType.Markdown, JsonSerializer.Serialize(new { md = rest }, Json.Opts)));
        }

        return blocks;
    }

    private static string? ReadRange(string root, string path, ref int start, ref int end)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(root, path));
            if (lines.Length == 0)
            {
                return null;
            }

            start = Math.Clamp(start, 1, lines.Length);
            end = Math.Clamp(end, start, lines.Length);

            // Surgical excerpts: when the citation opens inside the file's
            // license/usings preamble (the model anchors at line 1 because it only
            // saw the file head), advance to the first real declaration so the
            // snippet shows code rather than a copyright block. Only advance when
            // the cited range actually reaches past the preamble.
            var firstReal = FirstSubstantiveLine(lines);
            if (start < firstReal && firstReal <= end)
            {
                start = firstReal;
            }

            end = Math.Min(end, Math.Min(lines.Length, start + MaxCitedLines - 1));
            return string.Join('\n', lines[(start - 1)..end]);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 1-based index of the first line past the leading license / using / preprocessor
    /// preamble — where real declarations (namespace, attributes, types) begin. Tuned
    /// for C-family syntax; returns 1 when no preamble is found (or the head is all
    /// preamble), so it never advances past everything.
    /// </summary>
    private static int FirstSubstantiveLine(string[] lines)
    {
        var inBlockComment = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (inBlockComment)
            {
                if (t.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }

                continue;
            }

            if (t.Length == 0
                || t.StartsWith("//", StringComparison.Ordinal)
                || t.StartsWith('#')
                || t.StartsWith("using ", StringComparison.Ordinal)
                || t.StartsWith("global using ", StringComparison.Ordinal))
            {
                continue;
            }

            if (t.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!t.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = true;
                }

                continue;
            }

            return i + 1;
        }

        return 1;
    }

    [GeneratedRegex(@"\{\{cite:([^:{}]+):(\d{1,6})-(\d{1,6})\}\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CitePattern();
}
