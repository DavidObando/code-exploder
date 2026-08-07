using System.Text.Json;
using CodeExploder.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeExploder.Pipeline.Tests;

/// <summary>
/// Citations resolve against the real workspace and produce SURGICAL excerpts:
/// a preamble-anchored range advances to the first real declaration, and no
/// excerpt exceeds the tight line cap (docs/01 §1.7, docs/10 §quality).
/// </summary>
public sealed class CitationResolverTests : IDisposable
{
    private const int MaxCitedLines = 36;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cite-tests-" + Guid.NewGuid().ToString("N"));

    public CitationResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private RepoMap Write(string path, string content)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        var file = new RepoFile(path, "C#", content.Length, false, null, FileRole.None, 0, 0);
        return new RepoMap([file], [], [], [], [], [], 0, 0, content.Length);
    }

    private static (int Start, int End, string Content) Cited(
        IReadOnlyList<(string Type, string DataJson)> blocks)
    {
        var code = blocks.Single(b => b.Type == BlockType.Code);
        using var doc = JsonDocument.Parse(code.DataJson);
        var r = doc.RootElement;
        return (r.GetProperty("startLine").GetInt32(),
            r.GetProperty("endLine").GetInt32(),
            r.GetProperty("content").GetString()!);
    }

    private static string CsFile()
    {
        // Lines 1-10 are license/usings/preprocessor preamble; the namespace (the
        // first real declaration) is line 11; the type is line 15.
        const string head =
            "// <copyright file=\"Binder.cs\" company=\"GSharp\">\n" +   // 1
            "// Copyright (C) GSharp Authors. All rights reserved.\n" + // 2
            "// </copyright>\n" +                                       // 3
            "\n" +                                                       // 4
            "using System;\n" +                                          // 5
            "using System.Collections.Generic;\n" +                     // 6
            "using System.Linq;\n" +                                     // 7
            "#pragma warning disable SA1600\n" +                        // 8
            "#nullable enable\n" +                                       // 9
            "\n" +                                                       // 10
            "namespace GSharp.Core.CodeAnalysis.Binding;\n" +           // 11
            "\n" +                                                       // 12
            "/// <summary>Binds syntax to symbols.</summary>\n" +       // 13
            "\n" +                                                       // 14
            "public sealed class Binder";                                // 15
        var body = string.Join('\n', Enumerable.Range(16, 90).Select(i => $"    // body line {i}"));
        return head + "\n" + body;
    }

    [Fact]
    public void PreambleAnchoredCitationAdvancesToFirstDeclaration()
    {
        var map = Write("src/Binder.cs", CsFile());

        var blocks = CitationResolver.Resolve(
            "Here is the binder.\n{{cite:src/Binder.cs:1-80}}\nDone.",
            _root, map, NullLogger.Instance);

        var (start, end, content) = Cited(blocks);
        Assert.Equal(11, start); // the namespace line, past the license + usings
        Assert.DoesNotContain("copyright", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("using System", content, StringComparison.Ordinal);
        Assert.Contains("class Binder", content, StringComparison.Ordinal);
        Assert.True(end - start + 1 <= MaxCitedLines);
    }

    [Fact]
    public void OverlongRangeIsClampedToTheCap()
    {
        var map = Write("src/Binder.cs", CsFile());

        var blocks = CitationResolver.Resolve("{{cite:src/Binder.cs:1-500}}", _root, map, NullLogger.Instance);

        var (start, end, _) = Cited(blocks);
        Assert.Equal(MaxCitedLines, end - start + 1);
    }

    [Fact]
    public void DeliberateMidFileRangeIsRespected()
    {
        var map = Write("src/Binder.cs", CsFile());

        // The model cited a tight window well past the preamble — leave it alone.
        var blocks = CitationResolver.Resolve("{{cite:src/Binder.cs:40-55}}", _root, map, NullLogger.Instance);

        var (start, end, _) = Cited(blocks);
        Assert.Equal(40, start);
        Assert.Equal(55, end);
    }

    [Fact]
    public void UnknownFileCitationIsDroppedNotInvented()
    {
        var map = Write("src/Binder.cs", CsFile());

        var blocks = CitationResolver.Resolve(
            "text {{cite:src/Ghost.cs:1-10}} more", _root, map, NullLogger.Instance);

        Assert.DoesNotContain(blocks, b => b.Type == BlockType.Code);
        Assert.Equal(2, blocks.Count(b => b.Type == BlockType.Markdown));
    }

    [Fact]
    public void FileWithNoPreambleIsUnaffected()
    {
        var body = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var map = Write("data/notes.txt", body);

        var blocks = CitationResolver.Resolve("{{cite:data/notes.txt:1-10}}", _root, map, NullLogger.Instance);

        var (start, end, _) = Cited(blocks);
        Assert.Equal(1, start);
        Assert.Equal(10, end);
    }
}
