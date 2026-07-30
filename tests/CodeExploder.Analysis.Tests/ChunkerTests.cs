using System.Text;
using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class ChunkerTests
{
    private readonly Chunker _chunker = new();

    [Fact]
    public void EmptyContentYieldsNoChunks()
    {
        Assert.Empty(_chunker.ChunkFile("a.cs", "", "C#"));
    }

    [Fact]
    public void SmallFileYieldsOneChunkCoveringAllLines()
    {
        var content = "public class A\n{\n    void M() { }\n}\n";

        var chunks = _chunker.ChunkFile("a.cs", content, "C#");

        var chunk = Assert.Single(chunks);
        Assert.Equal(1, chunk.StartLine);
        Assert.Equal(4, chunk.EndLine);
        Assert.Equal("public class A\n{\n    void M() { }\n}", chunk.Content);
        Assert.Equal(chunk.Content.Length / 4, chunk.TokenCount);
    }

    [Fact]
    public void LargeFileSplitsAtDeclarationBoundariesWithinLimits()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            sb.Append("public class C").Append(i).Append('\n').Append("{\n");
            for (var j = 0; j < 30; j++)
            {
                sb.Append("    // filler filler filler filler\n");
            }

            sb.Append("}\n");
        }

        var content = sb.ToString();
        var totalLines = 20 * 33;

        var chunks = _chunker.ChunkFile("big.cs", content, "C#");

        Assert.True(chunks.Count >= 2);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(totalLines, chunks[^1].EndLine);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            Assert.InRange(chunks[i].Content.Length, 8_000, 16_000);
            Assert.Equal(chunks[i].EndLine + 1, chunks[i + 1].StartLine);
            Assert.StartsWith("public class", chunks[i + 1].Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoBoundaryFileFallsBackToFixedWindowsWithOverlap()
    {
        var content = string.Join('\n', Enumerable.Range(1, 700).Select(i => $"; filler line {i}"));

        var chunks = _chunker.ChunkFile("data.txt", content, "Text");

        Assert.Equal(3, chunks.Count);
        Assert.Equal((1, 300), (chunks[0].StartLine, chunks[0].EndLine));
        Assert.Equal((281, 580), (chunks[1].StartLine, chunks[1].EndLine));
        Assert.Equal((561, 700), (chunks[2].StartLine, chunks[2].EndLine));
    }

    [Fact]
    public void MarkdownSplitsAtHeadings()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 6; i++)
        {
            sb.Append("# Section ").Append(i).Append('\n');
            for (var j = 0; j < 40; j++)
            {
                sb.Append("lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod\n");
            }
        }

        var chunks = _chunker.ChunkFile("guide.md", sb.ToString(), "Markdown");

        Assert.True(chunks.Count >= 2);
        for (var i = 1; i < chunks.Count; i++)
        {
            Assert.StartsWith("# Section", chunks[i].Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ForceCutOverlapsTwentyLinesIntoNextChunk()
    {
        // One boundary at the top and one at the bottom; the middle is long enough to
        // force a hard cut at 16k chars with no boundary in reach.
        var sb = new StringBuilder();
        sb.Append("public class Big\n");
        for (var i = 0; i < 300; i++)
        {
            sb.Append("// ").Append(new string('x', 97)).Append('\n');
        }

        sb.Append("public class End\n");

        var chunks = _chunker.ChunkFile("wall.cs", sb.ToString(), "C#");

        Assert.True(chunks.Count >= 2);
        Assert.Equal(chunks[0].EndLine - 19, chunks[1].StartLine);
        Assert.Equal(302, chunks[^1].EndLine);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            Assert.True(chunks[i + 1].StartLine <= chunks[i].EndLine + 1, "chunks must cover the file in order");
        }
    }

    [Fact]
    public void ChunkLineNumbersAreOneBasedAndContiguous()
    {
        var content = string.Join('\n', Enumerable.Range(1, 950).Select(i => $"; row {i}"));

        IReadOnlyList<CodeChunk> chunks = _chunker.ChunkFile("rows.txt", content, "Text");

        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(950, chunks[^1].EndLine);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            Assert.True(chunks[i + 1].StartLine <= chunks[i].EndLine + 1);
            Assert.True(chunks[i + 1].StartLine > chunks[i].StartLine);
        }
    }
}
