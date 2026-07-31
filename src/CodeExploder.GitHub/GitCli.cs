using System.Diagnostics;
using System.Text;
using CodeExploder.Domain;
using Microsoft.Extensions.Logging;

namespace CodeExploder.GitHub;

public sealed record CloneResult(string CommitSha);

/// <summary>Thrown for user-facing acquisition failures (repo too large, unreachable, …).</summary>
public sealed class AcquireException(string message) : Exception(message);

/// <summary>
/// Process-based git wrapper for S0 acquire (docs/01-analysis-pipeline.md): shallow
/// single-branch clones of public GitHub repos, PR-head fetch, and history stats.
/// Credential prompts are disabled so private/nonexistent repos fail fast instead of
/// hanging; clones never execute repo-provided code.
/// </summary>
public sealed class GitCli(ILogger<GitCli> logger)
{
    public const int CloneDepth = 200;
    public const long MaxWorkspaceBytes = 1L * 1024 * 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public async Task<CloneResult> CloneAsync(
        string url, string workspacePath, string? gitRef, int? prNumber, CancellationToken ct)
    {
        if (Directory.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);

        var cloneArgs = new List<string> { "clone", "--depth", CloneDepth.ToString(), "--single-branch" };
        if (!string.IsNullOrWhiteSpace(gitRef) && prNumber is null)
        {
            cloneArgs.AddRange(["--branch", gitRef]);
        }

        cloneArgs.AddRange([url, workspacePath]);
        await RunAsync(null, cloneArgs, ct);

        if (prNumber is { } pr)
        {
            await RunAsync(workspacePath, ["fetch", "--depth", CloneDepth.ToString(), "origin", $"pull/{pr}/head:pr-head"], ct);
            await RunAsync(workspacePath, ["checkout", "pr-head"], ct);
        }

        var size = DirectorySizeBytes(workspacePath);
        if (size > MaxWorkspaceBytes)
        {
            Directory.Delete(workspacePath, recursive: true);
            throw new AcquireException(
                $"Repository checkout is {size / (1024 * 1024)} MB, over the {MaxWorkspaceBytes / (1024 * 1024)} MB limit.");
        }

        var sha = (await RunAsync(workspacePath, ["rev-parse", "HEAD"], ct)).Trim();
        return new CloneResult(sha);
    }

    /// <summary>
    /// Unified diff of the PR head against its merge-base with the default branch
    /// (falls back to the branch tip when the shallow history lacks the merge-base).
    /// </summary>
    public async Task<string> DiffPrAsync(string workspacePath, CancellationToken ct)
    {
        var defaultRef = (await RunAsync(workspacePath, ["rev-parse", "--abbrev-ref", "origin/HEAD"], ct)).Trim();
        string baseRef;
        try
        {
            baseRef = (await RunAsync(workspacePath, ["merge-base", defaultRef, "HEAD"], ct)).Trim();
        }
        catch (AcquireException)
        {
            // Shallow clone may not contain the merge-base; branch-tip diff includes
            // upstream drift noise but stays correct for the PR's own hunks.
            baseRef = defaultRef;
        }

        return await RunAsync(workspacePath, ["diff", "--no-color", "-M", $"{baseRef}..HEAD"], ct);
    }

    /// <summary>
    /// Converts the shallow clone to full history without blob content (tree/commit
    /// metadata only — all the story miner needs). Idempotent: already-complete
    /// clones are a no-op.
    /// </summary>
    public async Task UnshallowAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            await RunAsync(workspacePath, ["fetch", "--unshallow", "--filter=blob:none", "origin"], ct);
        }
        catch (AcquireException ex) when (ex.Message.Contains("complete", StringComparison.OrdinalIgnoreCase))
        {
            // "fatal: --unshallow on a complete repository does not make sense"
        }
    }

    /// <summary>
    /// Full commit log oldest-first with touched paths, capped at
    /// <paramref name="maxCommits"/> most recent commits. Rename detection is off so
    /// blobless partial clones need no content fetches.
    /// </summary>
    public async Task<string> FullLogAsync(string workspacePath, int maxCommits, CancellationToken ct) =>
        await RunAsync(workspacePath,
        [
            "log", "--reverse", "--no-renames", "--name-only", "--date=iso-strict",
            $"--max-count={maxCommits}", "--pretty=format:@@%H|%an|%ad|%s",
        ], ct);

    /// <summary>Commit/contributor counts and per-file churn over the shallow history window.</summary>
    public async Task<GitStats> CollectStatsAsync(string workspacePath, CancellationToken ct)
    {
        var authorsOut = await RunAsync(workspacePath, ["log", "--pretty=format:%an"], ct);
        var authors = authorsOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var churn = new Dictionary<string, int>(StringComparer.Ordinal);
        var filesOut = await RunAsync(workspacePath, ["log", "--name-only", "--pretty=format:"], ct);
        foreach (var line in filesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            churn[line] = churn.GetValueOrDefault(line) + 1;
        }

        return new GitStats(
            authors.Length,
            authors.Distinct(StringComparer.Ordinal).Count(),
            churn);
    }

    private async Task<string> RunAsync(string? workingDir, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (workingDir is not null)
        {
            psi.WorkingDirectory = workingDir;
        }

        // No interactive credential prompts, no helper fallback: a private or missing
        // repo must fail immediately with git's own error.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "true";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new AcquireException($"git {args[0]} timed out or was cancelled.");
        }

        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            logger.LogWarning("git {Args} failed ({Code}): {Error}", string.Join(' ', args), process.ExitCode, error);
            throw new AcquireException($"git {args[0]} failed: {Truncate(error, 400)}");
        }

        return stdout.ToString();
    }

    internal static long DirectorySizeBytes(string root) =>
        new DirectoryInfo(root)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(f => f.Length);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
