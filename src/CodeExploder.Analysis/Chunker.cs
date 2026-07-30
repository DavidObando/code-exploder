using System.Text.RegularExpressions;
using CodeExploder.Domain;

namespace CodeExploder.Analysis;

/// <summary>
/// S2: splits a file into retrieval-sized chunks (~2–4k tokens ≈ 8k–16k chars) at
/// top-level declaration boundaries detected by a per-language regex, with a fixed
/// 300-line window fallback for boundary-free files (docs/01 §S2).
/// </summary>
public sealed partial class Chunker
{
    internal const int DefaultSoftMaxChars = 8_000;
    internal const int DefaultHardMaxChars = 16_000;
    private const int OverlapLines = 20;
    private const int FallbackWindowLines = 300;

    private readonly int _softMaxChars;
    private readonly int _hardMaxChars;

    public Chunker()
        : this(DefaultSoftMaxChars, DefaultHardMaxChars)
    {
    }

    internal Chunker(int softMaxChars, int hardMaxChars)
    {
        _softMaxChars = softMaxChars;
        _hardMaxChars = hardMaxChars;
    }

    [GeneratedRegex(@"^ {0,4}(?:(?:export|public|private|protected|internal|static|abstract|sealed|partial)\s+)*(?:class|interface|enum|struct|record|impl|trait|module|namespace|type|func|fn|def|function|sub|void|int|string|bool|var|let|const)\b")]
    private static partial Regex DeclarationRegex();

    [GeneratedRegex("^#{1,6} ")]
    private static partial Regex MarkdownHeadingRegex();

    /// <summary>
    /// Chunks <paramref name="content"/> in order, covering the whole file.
    /// Line numbers are 1-based inclusive. Empty content yields an empty list.
    /// </summary>
    public IReadOnlyList<CodeChunk> ChunkFile(string relativePath, string content, string language)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var lines = SplitLines(content);
        var isMarkdown = string.Equals(language, "Markdown", StringComparison.OrdinalIgnoreCase);

        var boundaries = new bool[lines.Count];
        var hasSplitPoint = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (DeclarationRegex().IsMatch(lines[i]) || (isMarkdown && MarkdownHeadingRegex().IsMatch(lines[i])))
            {
                boundaries[i] = true;
                if (i > 0)
                {
                    hasSplitPoint = true;
                }
            }
        }

        return hasSplitPoint ? BoundaryChunks(relativePath, lines, boundaries) : FallbackChunks(relativePath, lines);
    }

    private List<CodeChunk> BoundaryChunks(string path, List<string> lines, bool[] boundaries)
    {
        var chunks = new List<CodeChunk>();
        var start = 0;
        var chars = 0;
        var i = 0;
        while (i < lines.Count)
        {
            chars += lines[i].Length + 1;

            if (i == lines.Count - 1)
            {
                chunks.Add(MakeChunk(path, lines, start, i));
                break;
            }

            if (chars >= _hardMaxChars)
            {
                // Force-cut with a 20-line overlap into the next chunk.
                chunks.Add(MakeChunk(path, lines, start, i));
                start = Math.Max(start + 1, i + 1 - OverlapLines);
                i = start;
                chars = 0;
                continue;
            }

            if (chars >= _softMaxChars && boundaries[i + 1])
            {
                chunks.Add(MakeChunk(path, lines, start, i));
                start = i + 1;
                i = start;
                chars = 0;
                continue;
            }

            i++;
        }

        return chunks;
    }

    private static List<CodeChunk> FallbackChunks(string path, List<string> lines)
    {
        var chunks = new List<CodeChunk>();
        var start = 0;
        while (true)
        {
            var end = Math.Min(start + FallbackWindowLines - 1, lines.Count - 1);
            chunks.Add(MakeChunk(path, lines, start, end));
            if (end == lines.Count - 1)
            {
                break;
            }

            start = end + 1 - OverlapLines;
        }

        return chunks;
    }

    private static CodeChunk MakeChunk(string path, List<string> lines, int start, int end)
    {
        var content = string.Join('\n', lines.GetRange(start, end - start + 1));
        return new CodeChunk(path, start + 1, end + 1, content, content.Length / 4);
    }

    private static List<string> SplitLines(string content)
    {
        var lines = new List<string>(content.Split('\n'));
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            // A trailing newline does not open a new (empty) line.
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
